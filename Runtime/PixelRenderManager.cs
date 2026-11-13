using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EliotByte.PixelRender
{
	/// <summary>
	/// Пиксельный рендер (камера): все неподвижные объекты слоя Layer рисуются крупными квадратными пикселями.
	/// Рендерит террейн в буфер источника (_PixelRenderSource) и рисует квад финальной сборки (Resolve) со ступенчатым краем.
	///
	/// Стабильные области — тайлы TileSize×TileSize м по XZ: у каждого тайла своя запечённая сетка
	/// (матрица «мир → экран» кадра запекания). Тайл перепекается, когда его клетки на экране исказились
	/// (повёрнуты, растянуты, сжаты) больше чем на BakeMaxError пикселей — ближние тайлы часто, дальние редко.
	/// </summary>
	[ExecuteAlways]
	[RequireComponent(typeof(Camera))]
	[AddComponentMenu("Pixel Render/Pixel Render Manager")]
	public class PixelRenderManager : MonoBehaviour
	{
		/// <summary>Слой пиксельных объектов: их рисует только камера источника (Legacy-рендер — на слое 31).</summary>
		public const int Layer = 30;

		[Header("Pixel Grid Settings")]
		[Tooltip("Размер крупного пикселя на экране (например, 7-8).")]
		[Range(2, 16)] public float CellPixels = 7f;

		[Header("Tiles (stable regions)")]
		[Tooltip("Размер тайла в метрах. У каждого тайла своя запечённая сетка.")]
		[Min(2f)] public float TileSize = 16f;

		[Tooltip("Перепекать тайл, когда угол клетки в нём сместился (поворот, растяжение, сжатие) больше чем на столько пикселей.")]
		[Range(0.1f, 4f)] public float BakeMaxError = 1f;

		[Tooltip("Вместе с тайлом, который перепекается, перепекаются тайлы с ошибкой больше этой доли от Bake Max Error — " +
		         "у них общая сетка и нет шва между ними.")]
		[Range(0f, 1f)] public float BakeGroup = 0.5f;

		[Header("Edge quads")]
		[Tooltip("Квады на клетках, которые режут рёбра полигонов: закрывают разрез целой клеткой.")]
		public bool EdgeQuads = true;

		[Tooltip("Запас квада на ребре (доля клетки).")]
		[Range(0f, 0.25f)] public float EdgePadding = 0.08f;

		[Tooltip("Сдвиг квада на ребре к камере (доля расстояния) — чтобы он лёг поверх разреза.")]
		[Range(0f, 0.05f)] public float EdgeBias = 0.02f;

		[Header("Resolve")]
		[Tooltip("Допуск глубины (доля) при проверке «центр клетки не за силуэтом».")]
		[Range(0.005f, 0.2f)] public float DepthTolerance = 0.03f;

		[Header("References")]
		[SerializeField] private Shader resolveShader;
		[SerializeField] private ComputeShader edgeCompute;

		/// <summary>Отладка (тест): перепекать сетку каждый кадр — сетка совпадает с экранной.</summary>
		[System.NonSerialized] public bool DebugForceBake;
		/// <summary>Отладка (тест): не рисовать сборку — остаётся только фон.</summary>
		[System.NonSerialized] public bool DebugHideResolve;
		/// <summary>Счётчик кадров с перепеканием (для тестов).</summary>
		[System.NonSerialized] public int BakeCount;
		/// <summary>Счётчик перепечённых тайлов (для тестов).</summary>
		[System.NonSerialized] public int TileBakeCount;
		/// <summary>Отладка сборки: 1 — раскраска причин, по которым пиксель не проходит проверки.</summary>
		[System.NonSerialized] public int DebugResolveMode;
		/// <summary>Кадр последнего перепекания тайла (номер BakeCount).</summary>
		public int TileStamp(int index) => _tiles[index].Stamp;

		/// <summary>Число тайлов.</summary>
		public int TileCount => _tiles.Length;

		private Camera _camera;
		private Camera _source;
		private RenderTexture _sourceRT;

		private struct TileSample
		{
			public Vector3 Position, TangentU, TangentV;
		}

		private class Tile
		{
			public Matrix4x4 Bake, Inverse;
			public int Stamp;
			public bool Baked;
			public float Error;
			public readonly List<TileSample> Samples = new();
		}

		private Tile[] _tiles = Array.Empty<Tile>();
		private Vector2 _tileOrigin;
		private Vector2Int _tileCount;
		private float _tileSize;
		private int _terrainKey;
		private readonly List<MeshFilter> _terrainFilters = new();
		private double _nextTerrainSearch;
		private ComputeBuffer _bakeBuffer, _inverseBuffer, _stampBuffer;
		private float[] _stampData = Array.Empty<float>();

		private Matrix4x4[] _bakeData = Array.Empty<Matrix4x4>(), _inverseData = Array.Empty<Matrix4x4>();
		private float _surfaceCell;
		private bool _surfaceBaked;

		// Квады на рёбрах полигонов.
		private const int MaxEdgeCells = 131072;
		private const int TriangleStride = 29 * 4, EdgeCellStride = 24 * 4;
		private ComputeBuffer _triangleBuffer, _edgeCellBuffer, _edgeCountBuffer;
		private int _triangleCount;
		private int _edgesStamp = -1;
		private float _edgesCell;
		private readonly List<Material> _edgesMaterials = new();
		private readonly List<GameObject> _edgesObjects = new();
		private Mesh _edgesMesh;
		/// <summary>Сколько клеток на рёбрах нашлось в последний раз (для тестов; читается с видеокарты по запросу).</summary>
		public int ReadEdgeCellCount()
		{
			if (_edgeCountBuffer == null) return 0;
			var data = new int[1];
			_edgeCountBuffer.GetData(data);
			return data[0];
		}

		private Material _resolveMaterial;
		private GameObject _quadObject;
		private Transform _quadTransform;
		private MeshRenderer _quadRenderer;
		private MaterialPropertyBlock _quadBlock;

		private static readonly int SourceId = Shader.PropertyToID("_PixelRenderSource");
		private static readonly int SourceSizeId = Shader.PropertyToID("_PixelRenderSourceSize");
		private static readonly int SourcePassId = Shader.PropertyToID("_PixelRenderSourcePass");
		private static readonly int PixelIdId = Shader.PropertyToID("_PixelRenderId");

		private static readonly int TileBakeId = Shader.PropertyToID("_PixelRenderTileBake");
		private static readonly int TileBakeInverseId = Shader.PropertyToID("_PixelRenderTileBakeInverse");
		private static readonly int EdgeObjectId = Shader.PropertyToID("_EdgeObject");
		private static readonly int EdgeCellsId = Shader.PropertyToID("_PixelRenderEdgeCells");
		private static readonly int EdgeCountId = Shader.PropertyToID("_PixelRenderEdgeCount");
		private static readonly int EdgeParamsId = Shader.PropertyToID("_PixelRenderEdgeParams");
		private static readonly int TileStampId = Shader.PropertyToID("_PixelRenderTileStamp");
		private static readonly int TileGridId = Shader.PropertyToID("_PixelRenderTileGrid");
		private static readonly int TileCountId = Shader.PropertyToID("_PixelRenderTileCount");
		private static readonly int ResolveParamsId = Shader.PropertyToID("_PixelRenderResolveParams");
		private static readonly int SurfaceCellId = Shader.PropertyToID("_PixelRenderSurfaceCell");

		private static readonly int ScreenMatrixId = Shader.PropertyToID("_PixelRenderScreenMatrix");
		private static readonly int ScreenInverseId = Shader.PropertyToID("_PixelRenderScreenInverse");
		private static readonly int CameraPositionId = Shader.PropertyToID("_PixelRenderCameraPosition");
		private static readonly int CameraForwardId = Shader.PropertyToID("_PixelRenderCameraForward");
		private static readonly int GridId = Shader.PropertyToID("_Grid");

		private void OnEnable()
		{
			Initialize();
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
			RenderPipelineManager.endCameraRendering += OnEndCamera;
		}

		private void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			RenderPipelineManager.endCameraRendering -= OnEndCamera;
			Release();
		}

		private void Initialize()
		{
			if (_camera == null)
				_camera = GetComponent<Camera>();

			if (resolveShader == null)
				resolveShader = Shader.Find("EliotByte/PixelRender/Resolve");

			if (_resolveMaterial == null && resolveShader != null)
			{
				_resolveMaterial = new Material(resolveShader) { hideFlags = HideFlags.HideAndDontSave };
			}

			_camera.cullingMask &= ~(1 << Layer);

			if (_source == null)
			{
				var sourceObj = new GameObject("Pixel Render Pixel Source Camera") { hideFlags = HideFlags.HideAndDontSave };
				_source = sourceObj.AddComponent<Camera>();
				_source.cullingMask = 1 << Layer;
				_source.clearFlags = CameraClearFlags.SolidColor;
				_source.backgroundColor = new Color(0, 0, 0, 0);
				_source.allowMSAA = false;
				_source.allowHDR = false;
				_source.useOcclusionCulling = false;

				var data = _source.GetUniversalAdditionalCameraData();
				data.renderPostProcessing = false;
				data.antialiasing = AntialiasingMode.None;
				data.requiresColorOption = CameraOverrideOption.Off;
				data.requiresDepthOption = CameraOverrideOption.Off;
			}

			if (_quadObject == null)
			{
				_quadObject = new GameObject("Pixel Render Surface Resolve Quad") { hideFlags = HideFlags.HideAndDontSave };
				var filter = _quadObject.AddComponent<MeshFilter>();
				filter.sharedMesh = CreateUnitQuad();
				_quadRenderer = _quadObject.AddComponent<MeshRenderer>();
				_quadRenderer.sharedMaterial = _resolveMaterial;
				_quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
				_quadRenderer.receiveShadows = false;
				_quadTransform = _quadObject.transform;
				_quadBlock = new MaterialPropertyBlock();
			}
		}

		private static Mesh CreateUnitQuad()
		{
			var mesh = new Mesh { name = "Pixel Render Unit Quad", hideFlags = HideFlags.HideAndDontSave };
			mesh.vertices = new[]
			{
				new Vector3(-0.5f, -0.5f, 0f),
				new Vector3(0.5f, -0.5f, 0f),
				new Vector3(0.5f, 0.5f, 0f),
				new Vector3(-0.5f, 0.5f, 0f)
			};
			mesh.uv = new[]
			{
				new Vector2(0f, 0f),
				new Vector2(1f, 0f),
				new Vector2(1f, 1f),
				new Vector2(0f, 1f)
			};
			mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
			mesh.RecalculateNormals();
			mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
			return mesh;
		}

		private void Release()
		{
			if (_source != null)
			{
				_source.targetTexture = null;
				if (Application.isPlaying) Destroy(_source.gameObject); else DestroyImmediate(_source.gameObject);
				_source = null;
			}

			if (_quadObject != null)
			{
				if (Application.isPlaying) Destroy(_quadObject); else DestroyImmediate(_quadObject);
				_quadObject = null;
			}

			ReleaseRT(ref _sourceRT);
			_bakeBuffer?.Release();
			_inverseBuffer?.Release();
			_stampBuffer?.Release();
			_bakeBuffer = _inverseBuffer = _stampBuffer = null;
			_triangleBuffer?.Release();
			_edgeCellBuffer?.Release();
			_edgeCountBuffer?.Release();
			_triangleBuffer = _edgeCellBuffer = _edgeCountBuffer = null;
			_edgesStamp = -1;

			foreach (var edgesObject in _edgesObjects)
				if (edgesObject != null)
				{
					if (Application.isPlaying) Destroy(edgesObject); else DestroyImmediate(edgesObject);
				}

			_edgesObjects.Clear();

			if (_edgesMesh != null)
			{
				if (Application.isPlaying) Destroy(_edgesMesh); else DestroyImmediate(_edgesMesh);
				_edgesMesh = null;
			}

			foreach (var edgesMaterial in _edgesMaterials)
				if (edgesMaterial != null)
				{
					if (Application.isPlaying) Destroy(edgesMaterial); else DestroyImmediate(edgesMaterial);
				}

			_edgesMaterials.Clear();
			_tiles = Array.Empty<Tile>();
			_terrainKey = 0;

			if (_resolveMaterial != null)
			{
				if (Application.isPlaying) Destroy(_resolveMaterial); else DestroyImmediate(_resolveMaterial);
				_resolveMaterial = null;
			}

			_surfaceBaked = false;
		}

		private static void ReleaseRT(ref RenderTexture rt)
		{
			if (rt == null) return;
			rt.Release();
			if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
			rt = null;
		}

		private void LateUpdate()
		{
			Step();
		}

		private static readonly Unity.Profiling.ProfilerMarker StepMarker = new("PixelRender.Step");

		public void Step()
		{
			using var marker = StepMarker.Auto();
			Initialize();
			if (_camera == null || _source == null) return;

			_camera.cullingMask &= ~(1 << Layer);

			int width = Mathf.Max(1, _camera.pixelWidth);
			int height = Mathf.Max(1, _camera.pixelHeight);

			if (_sourceRT == null || _sourceRT.width != width || _sourceRT.height != height)
			{
				if (_source != null)
					_source.targetTexture = null;

				ReleaseRT(ref _sourceRT);
				_sourceRT = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
				{
					name = "Pixel Render Pixel Source",
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.HideAndDontSave
				};
				_sourceRT.Create();
			}

			_source.transform.SetPositionAndRotation(_camera.transform.position, _camera.transform.rotation);
			_source.fieldOfView = _camera.fieldOfView;
			_source.nearClipPlane = _camera.nearClipPlane;
			_source.farClipPlane = _camera.farClipPlane;
			_source.depth = _camera.depth - 1f;
			_source.targetTexture = _sourceRT;
			_source.aspect = width / (float)height;

			Matrix4x4 screenMatrix = _camera.projectionMatrix * _camera.worldToCameraMatrix;

			Shader.SetGlobalTexture(SourceId, _sourceRT);
			Shader.SetGlobalVector(SourceSizeId, new Vector4(width, height, 1f / width, 1f / height));

			UpdateSurfaceBake(screenMatrix, width, height);

			// Позиционируем квад сборки перед камерой
			float distance = _camera.nearClipPlane * 2f;
			float worldPerPixel = distance * 2f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / height;
			Transform cameraTransform = _camera.transform;
			_quadTransform.SetPositionAndRotation(_camera.ScreenToWorldPoint(new Vector3(width * 0.5f, height * 0.5f, distance)), cameraTransform.rotation);
			_quadTransform.localScale = new Vector3(width * worldPerPixel, height * worldPerPixel, 1f);

			_quadBlock.SetVector(GridId, new Vector4(0f, 0f, _surfaceCell, 1f));
			_quadRenderer.SetPropertyBlock(_quadBlock);
			_quadObject.SetActive(!DebugHideResolve);
		}

		/// <summary>Отрендерить источник сразу (для тестов вне Play: камера рендерится вручную).</summary>
		public void RenderSourceNow()
		{
			if (_source == null || _sourceRT == null) return;
			RenderPipeline.SubmitRenderRequest(_source, new RenderPipeline.StandardRequest { destination = _sourceRT });
		}

		public RenderTexture SourceTexture => _sourceRT;

		// ------------------------------------------------------------------ тайлы

		private void RefreshTerrain()
		{
			bool missing = _terrainFilters.Count == 0 || _terrainFilters.Exists(f => f == null);

			if (missing || Time.realtimeSinceStartupAsDouble > _nextTerrainSearch)
			{
				_nextTerrainSearch = Time.realtimeSinceStartupAsDouble + 2.0;
				_terrainFilters.Clear();

				foreach (var filter in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
					if (filter.gameObject.layer == Layer && filter.sharedMesh != null && filter.TryGetComponent(out MeshRenderer renderer) && renderer.enabled
					    && filter.GetComponentInParent<Rigidbody>() == null)
						_terrainFilters.Add(filter);
				// Движущиеся объекты (с Rigidbody) в тайлы и квады на рёбрах не входят: треугольники тайлов собираются
				// на CPU один раз и считаются неподвижными. Такие объекты рисуются только источником и сборкой.

				// Порядок стабильный (номер объекта = номер материала квадов на рёбрах; смена порядка перестроила бы тайлы).
				_terrainFilters.Sort((x, y) => x.GetHashCode().CompareTo(y.GetHashCode()));
			}

			int key = Mathf.RoundToInt(TileSize * 100f);

			foreach (var filter in _terrainFilters)
			{
				var mesh = filter.sharedMesh;
				key = key * 31 + mesh.GetHashCode();
				key = key * 31 + mesh.vertexCount;
				key = key * 31 + mesh.bounds.GetHashCode();
				key = key * 31 + filter.transform.localToWorldMatrix.GetHashCode();
			}

			if (key == _terrainKey && _tiles.Length > 0)
				return;

			_terrainKey = key;
			BuildTiles();
		}

		private void BuildTiles()
		{
			_tileSize = Mathf.Max(2f, TileSize);
			var bounds = new Bounds();
			bool any = false;

			foreach (var filter in _terrainFilters)
			{
				var rendererBounds = filter.GetComponent<MeshRenderer>().bounds;

				if (any) bounds.Encapsulate(rendererBounds);
				else bounds = rendererBounds;

				any = true;
			}

			if (!any)
			{
				_tiles = new[] { new Tile() };
				_tileOrigin = Vector2.zero;
				_tileCount = Vector2Int.one;
				_tileSize = 1e6f;
				return;
			}

			_tileOrigin = new Vector2(bounds.min.x, bounds.min.z);
			_tileCount = new Vector2Int(
				Mathf.Clamp(Mathf.CeilToInt(bounds.size.x / _tileSize), 1, 128),
				Mathf.Clamp(Mathf.CeilToInt(bounds.size.z / _tileSize), 1, 128));
			_tiles = new Tile[_tileCount.x * _tileCount.y];

			for (int i = 0; i < _tiles.Length; i++)
				_tiles[i] = new Tile();

			// Точки для оценки искажения: центры треугольников (с касательными), до 24 на тайл.
			var perTile = new List<TileSample>[_tiles.Length];
			var triangleData = new List<float>();
			int objectIndex = -1;
			var weightList = new List<Vector4>();

			foreach (var filter in _terrainFilters)
			{
				var mesh = filter.sharedMesh;
				var vertices = mesh.vertices;
				var triangles = mesh.triangles;
				var matrix = filter.transform.localToWorldMatrix;
				var uvs = mesh.uv;
				objectIndex++;
				weightList.Clear();
				mesh.GetUVs(1, weightList);

				for (int t = 0; t + 2 < triangles.Length; t += 3)
				{
					Vector3 a = matrix.MultiplyPoint3x4(vertices[triangles[t]]);
					Vector3 b = matrix.MultiplyPoint3x4(vertices[triangles[t + 1]]);
					Vector3 c = matrix.MultiplyPoint3x4(vertices[triangles[t + 2]]);
					Vector3 normal = Vector3.Cross(b - a, c - a);

					if (normal.sqrMagnitude < 1e-10f)
						continue;

					normal.Normalize();
					Vector3 center = (a + b + c) / 3f;
					Vector3 u = (b - a).normalized * 0.5f;
					Vector3 v = Vector3.Cross(normal, u).normalized * 0.5f;
					int index = TileIndexOf(center);

					// Треугольник для поиска клеток на рёбрах (порядок — как в меше: номер = SV_PrimitiveID).
					foreach (var p in new[] { a, b, c }) { triangleData.Add(p.x); triangleData.Add(p.y); triangleData.Add(p.z); }

					for (int k = 0; k < 3; k++)
					{
						Vector2 uv = uvs.Length > 0 ? uvs[triangles[t + k]] : Vector2.zero;
						triangleData.Add(uv.x); triangleData.Add(uv.y);
					}

					for (int k = 0; k < 3; k++)
					{
						Vector4 w = weightList.Count > 0 ? weightList[triangles[t + k]] : new Vector4(1, 0, 0, 0);
						triangleData.Add(w.x); triangleData.Add(w.y); triangleData.Add(w.z); triangleData.Add(w.w);
					}

					triangleData.Add(index);
					triangleData.Add(objectIndex);
					(perTile[index] ??= new List<TileSample>()).Add(new TileSample { Position = center, TangentU = u, TangentV = v });
				}
			}

			_triangleCount = triangleData.Count / 29;
			_triangleBuffer?.Release();
			_triangleBuffer = null;

			if (_triangleCount > 0)
			{
				_triangleBuffer = new ComputeBuffer(_triangleCount, TriangleStride);
				_triangleBuffer.SetData(triangleData);
			}

			_edgesStamp = -1;

			for (int i = 0; i < _tiles.Length; i++)
			{
				var list = perTile[i];

				if (list == null)
					continue;

				int step = Mathf.Max(1, list.Count / 24);

				for (int k = 0; k < list.Count; k += step)
					_tiles[i].Samples.Add(list[k]);
			}
		}

		private int TileIndexOf(Vector3 world)
		{
			int x = Mathf.Clamp(Mathf.FloorToInt((world.x - _tileOrigin.x) / _tileSize), 0, _tileCount.x - 1);
			int z = Mathf.Clamp(Mathf.FloorToInt((world.z - _tileOrigin.y) / _tileSize), 0, _tileCount.y - 1);
			return x + z * _tileCount.x;
		}

		private static bool ToPixels(Matrix4x4 matrix, Vector3 world, int width, int height, out Vector2 pixel)
		{
			Vector4 clip = matrix * new Vector4(world.x, world.y, world.z, 1f);
			pixel = Vector2.zero;

			if (clip.w <= 1e-4f)
				return false;

			pixel = new Vector2((clip.x / clip.w * 0.5f + 0.5f) * width, (clip.y / clip.w * 0.5f + 0.5f) * height);
			return true;
		}

		/// <summary>
		/// Ошибка тайла в пикселях: насколько смещается угол клетки, если перенести её из сетки запекания в текущий кадр.
		/// Для каждой точки — локальное отображение «экран запекания → текущий экран» на плоскости треугольника (2×2);
		/// без искажения это единичная матрица (сдвиг не считается — клетки приклеены к поверхности).
		/// </summary>
		private static float TileError(Tile tile, Matrix4x4 current, float cell, int width, int height)
		{
			float worst = 0f;

			float margin = cell * 2f;

			foreach (var sample in tile.Samples)
			{
				if (!ToPixels(current, sample.Position, width, height, out var c0))
					continue;

				if (c0.x < -margin || c0.y < -margin || c0.x > width + margin || c0.y > height + margin)
					continue;

				if (!ToPixels(tile.Bake, sample.Position, width, height, out var b0) ||
				    !ToPixels(tile.Bake, sample.Position + sample.TangentU, width, height, out var bu) ||
				    !ToPixels(tile.Bake, sample.Position + sample.TangentV, width, height, out var bv) ||
				    !ToPixels(current, sample.Position + sample.TangentU, width, height, out var cu) ||
				    !ToPixels(current, sample.Position + sample.TangentV, width, height, out var cv))
					return float.MaxValue;

				Vector2 b1 = bu - b0, b2 = bv - b0, c1 = cu - c0, c2 = cv - c0;
				float det = b1.x * b2.y - b2.x * b1.y;

				// Грань видна почти ребром (в запекании или сейчас) — клетки на ней вырождены, отображение неустойчиво.
				float detC = c1.x * c2.y - c2.x * c1.y;

				if (Mathf.Abs(det) < 0.3f * b1.magnitude * b2.magnitude || Mathf.Abs(detC) < 0.3f * c1.magnitude * c2.magnitude)
					continue;

				// A = C · B⁻¹
				float i00 = b2.y / det, i01 = -b2.x / det, i10 = -b1.y / det, i11 = b1.x / det;
				float a00 = c1.x * i00 + c2.x * i10 - 1f;
				float a01 = c1.x * i01 + c2.x * i11;
				float a10 = c1.y * i00 + c2.y * i10;
				float a11 = c1.y * i01 + c2.y * i11 - 1f;
				float error = cell * Mathf.Sqrt(a00 * a00 + a01 * a01 + a10 * a10 + a11 * a11);
				worst = Mathf.Max(worst, error);
			}

			return worst;
		}

		private void UpdateSurfaceBake(Matrix4x4 screenMatrix, int width, int height)
		{
			RefreshTerrain();
			// Целое число пикселей: при дробной клетке границы клеток проходят через центры пикселей, и квад на ребре и
			// сборка по-разному решают, чей это пиксель (линия в 1 px цвета соседней клетки, пунктир по границам).
			float cell = Mathf.Max(1f, Mathf.Round(CellPixels * (height / 720f)));
			bool all = DebugForceBake || !_surfaceBaked || !Mathf.Approximately(cell, _surfaceCell);
			float worst = 0f;

			foreach (var tile in _tiles)
			{
				tile.Error = !tile.Baked || all ? float.MaxValue : TileError(tile, screenMatrix, cell, width, height);
				worst = Mathf.Max(worst, tile.Error);
			}

			if (worst > BakeMaxError)
			{
				float group = BakeMaxError * BakeGroup;
				Matrix4x4 bake = screenMatrix;
				Matrix4x4 inverse = bake.inverse;
				BakeCount++;

				foreach (var tile in _tiles)
				{
					if (tile.Error <= group)
						continue;

					tile.Bake = bake;
					tile.Inverse = inverse;
					tile.Stamp = BakeCount;
					tile.Baked = true;
					TileBakeCount++;
				}
			}

			_surfaceCell = cell;
			_surfaceBaked = true;
			UploadTiles();
			UpdateEdges(width, height, cell);

			Transform camTransform = _camera.transform;
			Shader.SetGlobalFloat(SurfaceCellId, _surfaceCell);
			Shader.SetGlobalVector(TileGridId, new Vector4(_tileOrigin.x, _tileOrigin.y, _tileSize, 1f / _tileSize));
			Shader.SetGlobalVector(TileCountId, new Vector4(_tileCount.x, _tileCount.y, 0f, 0f));
			Shader.SetGlobalVector(ResolveParamsId, new Vector4(DepthTolerance, 0f, DebugResolveMode, EdgeQuads && !DebugHideResolve ? 1f : 0f));
			Shader.SetGlobalMatrix(ScreenMatrixId, screenMatrix);
			Shader.SetGlobalMatrix(ScreenInverseId, screenMatrix.inverse);
			Shader.SetGlobalVector(CameraPositionId, camTransform.position);
			Shader.SetGlobalVector(CameraForwardId, camTransform.forward);
		}

		// ------------------------------------------------------------------ квады на рёбрах

		/// <summary>
		/// Клетки, которые режут рёбра полигонов: пересчитываются на видеокарте, когда что-то перепеклось (список зависит
		/// только от запечённых сеток). Рисуются мешем квадов (квад строится в шейдере по номеру вершины).
		/// </summary>
		private void UpdateEdges(int width, int height, float cell)
		{
			if (edgeCompute == null)
				edgeCompute = Resources.Load<ComputeShader>("PixelRenderEdgeCells");

			bool active = EdgeQuads && edgeCompute != null && _triangleBuffer != null && !DebugHideResolve;
			EnsureEdgesObjects();

			foreach (var edgesObject in _edgesObjects)
				edgesObject.SetActive(active);

			if (!active)
				return;

			_edgeCellBuffer ??= new ComputeBuffer(MaxEdgeCells, EdgeCellStride, ComputeBufferType.Append);
			_edgeCountBuffer ??= new ComputeBuffer(1, 4, ComputeBufferType.Raw);

			if (_edgesStamp != BakeCount || !Mathf.Approximately(_edgesCell, cell))
			{
				_edgesStamp = BakeCount;
				_edgesCell = cell;
				int kernel = edgeCompute.FindKernel("FindEdgeCells");
				_edgeCellBuffer.SetCounterValue(0);
				edgeCompute.SetBuffer(kernel, "_Triangles", _triangleBuffer);
				edgeCompute.SetBuffer(kernel, "_EdgeCells", _edgeCellBuffer);
				edgeCompute.SetBuffer(kernel, "_PixelRenderTileBake", _bakeBuffer);
				edgeCompute.SetBuffer(kernel, "_PixelRenderTileBakeInverse", _inverseBuffer);
				edgeCompute.SetVector("_PixelRenderSourceSize", new Vector4(width, height, 1f / width, 1f / height));
				edgeCompute.SetFloat("_PixelRenderSurfaceCell", cell);
				edgeCompute.SetInt("_TriangleCount", _triangleCount);
				edgeCompute.SetInt("_MaxSteps", 2048);
				edgeCompute.Dispatch(kernel, Mathf.CeilToInt(_triangleCount * 3 / 64f), 1, 1);
				ComputeBuffer.CopyCount(_edgeCellBuffer, _edgeCountBuffer, 0);
			}

			Shader.SetGlobalBuffer(EdgeCellsId, _edgeCellBuffer);
			Shader.SetGlobalBuffer(EdgeCountId, _edgeCountBuffer);
			Shader.SetGlobalVector(EdgeParamsId, new Vector4(EdgePadding, EdgeBias, MaxEdgeCells, 0f));

			// У каждого объекта свой материал квадов — копия его материала (текстуры, шахматка, свет).
			for (int i = 0; i < _edgesMaterials.Count && i < _terrainFilters.Count; i++)
			{
				var objectMaterial = _terrainFilters[i].GetComponent<MeshRenderer>().sharedMaterial;

				if (objectMaterial != null)
					_edgesMaterials[i].CopyPropertiesFromMaterial(objectMaterial);

				_edgesMaterials[i].SetFloat(EdgeObjectId, i);
			}
		}

		private void EnsureEdgesObjects()
		{
			var shader = Shader.Find("EliotByte/PixelRender/Edges");

			if (shader == null)
				return;

			if (_edgesMesh == null)
			{
				// Вершины пустые: квад строится в вершинном шейдере по номеру вершины (4 на клетку).
				_edgesMesh = new Mesh { name = "Pixel Render Edge Quads", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
				_edgesMesh.SetVertexBufferParams(MaxEdgeCells * 4, new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
				_edgesMesh.SetVertexBufferData(new Vector3[MaxEdgeCells * 4], 0, 0, MaxEdgeCells * 4);
				var indices = new int[MaxEdgeCells * 6];

				for (int q = 0; q < MaxEdgeCells; q++)
				{
					int v = q * 4, i = q * 6;
					indices[i] = v; indices[i + 1] = v + 1; indices[i + 2] = v + 3;
					indices[i + 3] = v; indices[i + 4] = v + 3; indices[i + 5] = v + 2;
				}

				_edgesMesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
				_edgesMesh.SetIndexBufferData(indices, 0, 0, indices.Length);
				_edgesMesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length), MeshUpdateFlags.DontRecalculateBounds);
				_edgesMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
			}

			// По одному объекту квадов на каждый объект пиксельного рендера (свой материал).
			while (_edgesObjects.Count < _terrainFilters.Count)
			{
				var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
				var edgesObject = new GameObject("Pixel Render Edge Quads " + _edgesObjects.Count) { hideFlags = HideFlags.HideAndDontSave };
				edgesObject.AddComponent<MeshFilter>().sharedMesh = _edgesMesh;
				var renderer = edgesObject.AddComponent<MeshRenderer>();
				renderer.sharedMaterial = material;
				renderer.shadowCastingMode = ShadowCastingMode.Off;
				renderer.receiveShadows = false;
				_edgesMaterials.Add(material);
				_edgesObjects.Add(edgesObject);
			}

			while (_edgesObjects.Count > _terrainFilters.Count)
			{
				int last = _edgesObjects.Count - 1;
				if (Application.isPlaying) { Destroy(_edgesObjects[last]); Destroy(_edgesMaterials[last]); }
				else { DestroyImmediate(_edgesObjects[last]); DestroyImmediate(_edgesMaterials[last]); }
				_edgesObjects.RemoveAt(last);
				_edgesMaterials.RemoveAt(last);
			}
		}

		private void UploadTiles()
		{
			int count = Mathf.Max(1, _tiles.Length);

			if (_bakeBuffer == null || _bakeBuffer.count != count)
			{
				_bakeBuffer?.Release();
				_inverseBuffer?.Release();
				_stampBuffer?.Release();
				_bakeBuffer = new ComputeBuffer(count, 64);
				_inverseBuffer = new ComputeBuffer(count, 64);
				_stampBuffer = new ComputeBuffer(count, 4);
				_bakeData = new Matrix4x4[count];
				_inverseData = new Matrix4x4[count];
				_stampData = new float[count];
			}

			for (int i = 0; i < _tiles.Length; i++)
			{
				_bakeData[i] = _tiles[i].Bake;
				_inverseData[i] = _tiles[i].Inverse;
				_stampData[i] = _tiles[i].Stamp % 65536;
			}

			_bakeBuffer.SetData(_bakeData);
			_inverseBuffer.SetData(_inverseData);
			_stampBuffer.SetData(_stampData);
			Shader.SetGlobalBuffer(TileStampId, _stampBuffer);

			Shader.SetGlobalBuffer(TileBakeId, _bakeBuffer);
			Shader.SetGlobalBuffer(TileBakeInverseId, _inverseBuffer);
		}

		/// <summary>Центр тайла (для отладки).</summary>
		public Vector3 TileCenter(int index)
		{
			var samples = _tiles[index].Samples;
			if (samples.Count == 0) return Vector3.positiveInfinity;
			var sum = Vector3.zero;
			foreach (var sample in samples) sum += sample.Position;
			return sum / samples.Count;
		}

		/// <summary>Ошибка тайла по индексу (для отладки).</summary>
		public float TileErrorAt(int index) => index >= 0 && index < _tiles.Length ? _tiles[index].Error : 0f;

		private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
		{
			if (camera == _source)
			{
				var cmd = CommandBufferPool.Get("Pixel Render Source Pass Set");
				cmd.SetGlobalFloat(SourcePassId, 1f);
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
				Shader.SetGlobalFloat(SourcePassId, 1f);
			}
			else if (camera == _camera)
			{
				var cmd = CommandBufferPool.Get("Pixel Render Source Pass Reset");
				cmd.SetGlobalFloat(SourcePassId, 0f);
				context.ExecuteCommandBuffer(cmd);
				CommandBufferPool.Release(cmd);
				Shader.SetGlobalFloat(SourcePassId, 0f);
			}
		}

		private void OnEndCamera(ScriptableRenderContext context, Camera camera)
		{
			if (camera != _source) return;

			var cmd = CommandBufferPool.Get("Pixel Render Source Pass Reset");
			cmd.SetGlobalFloat(SourcePassId, 0f);
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
			Shader.SetGlobalFloat(SourcePassId, 0f);
		}
	}
}
