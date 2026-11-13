using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace EliotByte.PixelRender.Legacy
{
	/// <summary>
	/// Объект пиксельного рендера без импостеров (см. PixelRenderManager).
	/// Считает свою пиксельную сетку (якорь — центр объекта на экране, клетка — из плотности текселей)
	/// и выводит квад сборки по своему прямоугольнику на экране.
	/// Материал объекта — EliotByte/PixelRender/Source.
	/// </summary>
	[RequireComponent(typeof(MeshRenderer))]
	public class PixelRenderObject : MonoBehaviour
	{
		private static readonly int PixelIdId = Shader.PropertyToID("_PixelId");
		private static readonly int PixelCellId = Shader.PropertyToID("_PixelCell");
		private static readonly int PixelAnchorId = Shader.PropertyToID("_PixelAnchor");
		private static readonly int PixelSurfaceId = Shader.PropertyToID("_PixelSurface");
		private static readonly int SurfaceGridId = Shader.PropertyToID("_SurfaceGrid");
		private static readonly int PrevObjectToWorldId = Shader.PropertyToID("_PixelPrevObjectToWorld");
		private static readonly int PixelBakedId = Shader.PropertyToID("_PixelBaked");
		private static readonly int BakeProgressId = Shader.PropertyToID("_BakeProgress");
		private static readonly int ResolveBakedId = Shader.PropertyToID("_Baked");
		private static readonly int BakeMatrixId = Shader.PropertyToID("_BakeMatrix");
		private static readonly int BakeInverseId = Shader.PropertyToID("_BakeInverse");
		private static readonly int BakeGridId = Shader.PropertyToID("_BakeGrid");
		private static readonly int BakeMatrixOldId = Shader.PropertyToID("_BakeMatrixOld");
		private static readonly int BakeInverseOldId = Shader.PropertyToID("_BakeInverseOld");
		private static readonly int BakeGridOldId = Shader.PropertyToID("_BakeGridOld");
		private static readonly int BakedMaskId = Shader.PropertyToID("_BakedMask");
		private static readonly int BakeMaskId = Shader.PropertyToID("_BakeMask");
		private static readonly int BakeMaskRectId = Shader.PropertyToID("_BakeMaskRect");
		private static readonly int SpriteScaleId = Shader.PropertyToID("_SpriteScale");
		private static readonly int CenterDepthId = Shader.PropertyToID("_CenterDepth");
		private static readonly int BakeCenterDepthId = Shader.PropertyToID("_BakeCenterDepth");
		private static readonly int CaptureGridId = Shader.PropertyToID("_CaptureGrid");
		private static readonly int CaptureRectId = Shader.PropertyToID("_CaptureRect");
		private static readonly int CaptureFarDepthId = Shader.PropertyToID("_CaptureFarDepth");
		private static readonly int CaptureUpdateId = Shader.PropertyToID("_CaptureUpdate");
		private static readonly int CaptureCurrentId = Shader.PropertyToID("_CaptureCurrent");
		private static readonly int BakeMaskPrevId = Shader.PropertyToID("_BakeMaskPrev");
		private static readonly int CaptureLiveId = Shader.PropertyToID("_CaptureLive");
		private static readonly int CaptureHysteresisId = Shader.PropertyToID("_CaptureHysteresis");
		private static readonly int RectId = Shader.PropertyToID("_Rect");
		private static readonly int GridId = Shader.PropertyToID("_Grid");
		private static readonly int ChecksId = Shader.PropertyToID("_Checks");
		private static readonly int OutlineId = Shader.PropertyToID("_Outline");
		private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");

		[Header("Сетка")]
		[Tooltip("Сетка привязана к экрану (для пола и больших поверхностей), а не к центру объекта.")]
		public bool ScreenGrid;
		[Tooltip("Сетка на поверхности (для пола, террейна, стен — всего большого по глубине): экранная сетка камеры в момент " +
		         "запекания (квадратные клетки Screen Cell Pixels из общих настроек), приклеенная к поверхности; край — " +
		         "ступеньками. Запекание общее для всех таких объектов — см. «Сетка на поверхности» на Pixel Render Manager. " +
		         "Важнее Screen Grid.")]
		public bool SurfaceGrid;

		[Header("Настройки")]
		[Tooltip("Выключено — объект берёт общие настройки с Pixel Render Manager (на камере). " +
		         "Включено — свои, из полей ниже.")]
		public bool OwnSettings;

		[Header("Свои настройки (только с Own Settings)")]
		[Tooltip("Размер клетки для экранной сетки, в пикселях экрана при высоте 720 (масштабируется с высотой экрана).")]
		[Min(1f)] public float ScreenCellPixels = 7f;
		[Tooltip("Размер пикселя объекта в пикселях экрана при высоте 720 (масштабируется с высотой экрана: " +
		         "7 → ~1/100 высоты кадра, как на примере) — одинаковый у всех объектов на любом расстоянии.")]
		[Min(1f)] public float CellPixels = 7f;
		[Tooltip("Вместо Cell Pixels: пиксель = тексель (клетка шахматки) / Pixels Per Texel (крупные клетки → крупные пиксели).")]
		public bool AutoFromTexels;
		[Range(1, 8)] public int PixelsPerTexel = 3;
		[Tooltip("Минимальный размер пикселя объекта на экране (для Auto From Texels).")]
		[Min(1f)] public float MinScreenPixel = 2f;
		[Tooltip("Целое число пикселей экрана на пиксель объекта — пиксели ровные, без «то 2, то 3».")]
		public bool IntegerCells = false;
		[Tooltip("Размер пикселя масштабируется с высотой кадра (заданы для 720p). Выключено — ровно столько пикселей экрана.")]
		public bool ScaleWithScreen = true;

		[Header("Контур")]
		public bool Outline;
		public Color OutlineColor = new(0.06f, 0.06f, 0.08f, 1f);

		private MeshRenderer _renderer;
		private MaterialPropertyBlock _block;
		private int _id;
		private float _metersPerUv = 1f;
		private GameObject _quadObject;
		private Transform _quadTransform;
		private Mesh _quad;                 // общий единичный квад сборки (см. UnitQuad) — меш не меняется каждый кадр
		private static Mesh _unitQuad;
		private Bounds _meshBounds;         // рамка меша (кэш: углы для ошибки спрайта)
		private MaterialPropertyBlock _captureBlock, _updateBlock;
		private bool _maskSwapped;
		private readonly Vector3[] _cornersNow = new Vector3[8]; // углы рамки меша на экране в этом кадре
		private bool _cornersInFront;                              // все углы перед камерой          // в этом кадре маски поменялись местами под дозапись (см. UpdateGrid)

		/// <summary>Для замеров: не рисовать сборку объектов / пола (источник рисуется как обычно).</summary>
		public static bool DebugSkipResolve, DebugSkipSurfaceResolve;

		private static readonly ProfilerMarker GridMarker = new("PixelRender.Grid.Rect");
		private static readonly ProfilerMarker BakeMarker = new("PixelRender.Grid.Bake");
		private static readonly ProfilerMarker BlocksMarker = new("PixelRender.Grid.Blocks");
		private static readonly ProfilerMarker QuadMarker = new("PixelRender.Grid.Quad");
		private MeshRenderer _quadRenderer;
		private MaterialPropertyBlock _quadBlock;
		private static Material _resolveMaterial;
		private Matrix4x4 _previousObjectToWorld;
		private bool _hasPrevious;
		private readonly PixelRenderSettings _own = new();

		// «Запекание» ракурса: сетка объекта на момент запекания (новая и прошлая — для растворения).
		private struct BakedView
		{
			public Matrix4x4 ObjectToClip;
			public Vector4 Grid;
		}

		private BakedView _bakeNew, _bakeOld;
		private bool _baked;
		private int _bakeCount;
		private float _bakeProgress = 1f;
		private Vector3 _bakeDirection;
		private Vector3 _bakeUp;
		private float _bakeWorldCell;
		private float _bakeCenterDepth;
		private Vector2 _bakeAnchor;
		private readonly Vector2[] _bakeCorners = new Vector2[8]; // углы рамки меша на экране в момент запекания

		// Маска силуэта запечённого ракурса: клетки сетки запекания (занята / пусто / неизвестно).
		private RenderTexture _mask;
		private Vector4 _maskRect;
		private bool _maskValid;
		private bool _capturePending;
		private Rect _lastRect;
		private Vector2 _lastAnchor;
		private float _lastCenterDepth;
		private bool _visible;
		private RenderTexture _maskSpare;
		private Vector4 _maskUpdate;      // xy — якорь сейчас (округлённый), z — масштаб спрайта, w — глубина при запекании − сейчас
		private float _maskFarDepth;
		private bool _maskLive;
		private float _maskHysteresis;
		private static Mesh _fullscreenQuad;

		/// <summary>Для тестов: выключить дозапись масок.</summary>
		public static bool DisableMaskUpdates;

		/// <summary>Причины перезапеканий (для тестов).</summary>
		public static int BakesByError, BakesByThresholds;

		/// <summary>Сколько раз объект перезапекался (для тестов).</summary>
		public int BakeCount => _bakeCount;

		public int Id => _id;

		private void OnEnable() => Initialize();

		private void OnDisable() => Release();

		public void Initialize()
		{
			if (_quadObject != null)
				return;

			_renderer = GetComponent<MeshRenderer>();
			gameObject.layer = PixelRenderManager.Layer;
			_id = PixelRenderManager.TakeId();
			var sharedMesh = GetComponent<MeshFilter>().sharedMesh;
			_metersPerUv = MeasureMetersPerUv(sharedMesh);
			_meshBounds = sharedMesh != null ? sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
			_block = new MaterialPropertyBlock();
			_quadBlock = new MaterialPropertyBlock();
			_captureBlock = new MaterialPropertyBlock();
			_updateBlock = new MaterialPropertyBlock();

			if (_resolveMaterial == null)
				_resolveMaterial = new Material(Shader.Find("EliotByte/PixelRender/Legacy/Resolve")) { hideFlags = HideFlags.HideAndDontSave };

			_quad = UnitQuad();

			_quadObject = new GameObject(name + " Resolve") { hideFlags = HideFlags.HideAndDontSave };
			_quadObject.AddComponent<MeshFilter>().sharedMesh = _quad;
			_quadTransform = _quadObject.transform;
			_quadRenderer = _quadObject.AddComponent<MeshRenderer>();
			_quadRenderer.sharedMaterial = _resolveMaterial;
			_quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
			_quadRenderer.receiveShadows = false;
			_quadObject.SetActive(false);

			if (!PixelRenderManager.Objects.Contains(this))
				PixelRenderManager.Objects.Add(this);
		}

		public void Release()
		{
			if (_quadObject != null)
				PixelRenderManager.ReturnId(_id);

			PixelRenderManager.Objects.Remove(this);

			if (_quadObject != null)
			{
				if (Application.isPlaying)
					Destroy(_quadObject);
				else
					DestroyImmediate(_quadObject);
			}


			_quadObject = null;
			_hasPrevious = false;
			_baked = false;
			_bakeProgress = 1f;
			_maskValid = _capturePending = false;
			_maskSwapped = false;
			_visible = false;

			if (_mask != null)
			{
				_mask.Release();

				if (Application.isPlaying)
					Destroy(_mask);
				else
					DestroyImmediate(_mask);

				_mask = null;
			}

			if (_maskSpare != null)
			{
				_maskSpare.Release();

				if (Application.isPlaying)
					Destroy(_maskSpare);
				else
					DestroyImmediate(_maskSpare);

				_maskSpare = null;
			}
			_quad = null;
		}

		/// <summary>Сетка и квад сборки на этот кадр (вызывает PixelRenderManager).</summary>
		/// <summary>Настройки, которые действуют на объект: общие (с менеджера) или свои.</summary>
		public PixelRenderSettings GetSettings(PixelRenderSettings shared)
		{
			if (!OwnSettings && shared != null)
				return shared;

			_own.CellPixels = CellPixels;
			_own.ScreenCellPixels = ScreenCellPixels;
			_own.IntegerCells = IntegerCells;
			_own.ScaleWithScreen = ScaleWithScreen;
			_own.AutoFromTexels = AutoFromTexels;
			_own.PixelsPerTexel = PixelsPerTexel;
			_own.MinScreenPixel = MinScreenPixel;
			_own.Outline = Outline;
			_own.OutlineColor = OutlineColor;
			return _own;
		}

		/// <summary>Единичный квад (−0.5…0.5, лицом к камере), общий для всех объектов: прямоугольник сборки задаётся
		/// трансформом квада, а не перезаливкой вершин каждый кадр.</summary>
		private static Mesh UnitQuad()
		{
			if (_unitQuad != null)
				return _unitQuad;

			_unitQuad = new Mesh { name = "Pixel Resolve Quad", hideFlags = HideFlags.HideAndDontSave };
			_unitQuad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
			_unitQuad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
			_unitQuad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
			_unitQuad.RecalculateBounds();
			return _unitQuad;
		}

		private void Hide()
		{
			_visible = false;

			if (_quadObject.activeSelf)
				_quadObject.SetActive(false);
		}

		internal void UpdateGrid(Camera camera, PixelRenderSettings shared)
		{
			var settings = GetSettings(shared);

			if (_quadObject == null)
				return;

			GridMarker.Begin();
			Bounds bounds = _renderer.bounds;
			Vector3 center = bounds.center;
			float width = camera.pixelWidth;
			float height = camera.pixelHeight;
			float screenScale = settings.ScreenScale(height);
			Rect rect;
			Vector2 anchor;
			float cell;

			if (SurfaceGrid)
			{
				cell = Mathf.Max(1f, settings.ScreenCellPixels * screenScale);
				anchor = Vector2.zero;
				rect = new Rect(0f, 0f, width, height);
			}
			else if (ScreenGrid)
			{
				cell = Mathf.Max(1f, Mathf.Round(settings.ScreenCellPixels * screenScale));
				anchor = Vector2.zero;
				rect = new Rect(0f, 0f, width, height);
			}
			else
			{
				Vector3 anchorScreen = camera.WorldToScreenPoint(center);

				if (anchorScreen.z <= camera.nearClipPlane)
				{
					Hide();
					GridMarker.End();
					return;
				}

				float focal = 0.5f * height / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
				cell = settings.AutoFromTexels
					? Mathf.Max(settings.MinScreenPixel, EffectivePixelWorldSize(settings.PixelsPerTexel) * focal / anchorScreen.z)
					: Mathf.Max(1f, Mathf.Round(settings.CellPixels * screenScale));

				if (settings.IntegerCells)
					cell = Mathf.Max(1f, Mathf.Round(cell));

				anchor = anchorScreen;

				// Прямоугольник: 8 углов рамки меша на экране + запас в пару клеток (спрайт запекания может отставать от
				// геометрии на ~клетку). Раньше — описанная сфера: у вытянутых объектов квад был в разы больше и квады
				// сильно перекрывались — это была основная цена сборки на GPU. Угол позади камеры — по сфере, как раньше.
				Matrix4x4 objectToClip = ObjectToClip(camera);
				_cornersInFront = true;
				Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);

				for (int i = 0; i < 8; i++)
				{
					Vector3 corner = CornerOnScreen(objectToClip, i, width, height);
					_cornersNow[i] = corner;
					_cornersInFront &= corner.z > camera.nearClipPlane;
					min = Vector2.Min(min, corner);
					max = Vector2.Max(max, corner);
				}

				if (_cornersInFront)
				{
					rect = Rect.MinMaxRect(
						Mathf.Max(0f, min.x - cell * 2f), Mathf.Max(0f, min.y - cell * 2f),
						Mathf.Min(width, max.x + cell * 2f), Mathf.Min(height, max.y + cell * 2f));
				}
				else
				{
					float radius = bounds.extents.magnitude;
					float screenRadius = radius * focal / Mathf.Max(anchorScreen.z - radius, camera.nearClipPlane) + cell * 2f;
					rect = Rect.MinMaxRect(
						Mathf.Max(0f, anchor.x - screenRadius), Mathf.Max(0f, anchor.y - screenRadius),
						Mathf.Min(width, anchor.x + screenRadius), Mathf.Min(height, anchor.y + screenRadius));
				}

				if (rect.width <= 0f || rect.height <= 0f)
				{
					Hide();
					GridMarker.End();
					return;
				}
			}

			_lastRect = rect;
			_lastAnchor = anchor;
			_lastCenterDepth = Vector3.Dot(center - camera.transform.position, camera.transform.forward);
			_visible = true;
			GridMarker.End();
			BakeMarker.Begin();
			bool baked = !ScreenGrid && !SurfaceGrid && settings.SurfaceBake && UpdateBake(camera, settings, anchor, cell);
			BakeMarker.End();
			BlocksMarker.Begin();
			bool bakedMask = baked && settings.BakeSilhouette && (_maskValid || _capturePending)
			                 && (_bakeProgress >= 1f || settings.Transition == BakeTransition.Dissolve);

			// С маской силуэта цвет запечённого объекта берётся из картинки момента запекания, а источник рисует
			// объект обычной сеткой (из него эта картинка и снимается, и им закрываются «неизвестные» клетки).
			bool surfaceLattice = baked && !settings.BakeSilhouette;
			_block.SetFloat(PixelBakedId, surfaceLattice ? 1f : 0f);

			if (surfaceLattice)
			{
				bool morph = settings.Transition == BakeTransition.Morph;
				var current = morph ? CurrentView() : _bakeNew;
				float progress = morph || _bakeProgress >= 1f ? 2f : _bakeProgress;

				_block.SetMatrix(BakeMatrixId, current.ObjectToClip);
				_block.SetMatrix(BakeInverseId, current.ObjectToClip.inverse);
				_block.SetVector(BakeGridId, current.Grid);
				_block.SetMatrix(BakeMatrixOldId, _bakeOld.ObjectToClip);
				_block.SetMatrix(BakeInverseOldId, _bakeOld.ObjectToClip.inverse);
				_block.SetVector(BakeGridOldId, _bakeOld.Grid);
				_block.SetFloat(BakeProgressId, progress);
			}

			_block.SetFloat(PixelIdId, _id);
			_block.SetFloat(PixelSurfaceId, SurfaceGrid ? 1f : 0f);
			_block.SetFloat(PixelCellId, cell);
			_block.SetVector(PixelAnchorId, new Vector4(anchor.x, anchor.y, 0f, 0f));

			// Для гистерезиса: где объект был в прошлом кадре (точка поверхности ищется в истории через эту матрицу).
			Matrix4x4 objectToWorld = transform.localToWorldMatrix;
			_block.SetMatrix(PrevObjectToWorldId, _hasPrevious ? _previousObjectToWorld : objectToWorld);
			_previousObjectToWorld = objectToWorld;
			_hasPrevious = true;
			_renderer.SetPropertyBlock(_block);

			BlocksMarker.End();
			QuadMarker.Begin();
			// Квад сборки: прямоугольник rect на расстоянии 2×near перед камерой — единичный квад, растянутый трансформом.
			float distance = camera.nearClipPlane * 2f;
			float worldPerPixel = distance * 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / height;
			Transform cameraTransform = camera.transform;
			_quadTransform.SetPositionAndRotation(camera.ScreenToWorldPoint(new Vector3(rect.center.x, rect.center.y, distance)), cameraTransform.rotation);
			_quadTransform.localScale = new Vector3(rect.width * worldPerPixel, rect.height * worldPerPixel, 1f);

			// Дозапись маски — в «запасную» текстуру, затем они меняются ролями (без копирования): сборка этого кадра
			// уже смотрит в ту, куда будет записано.
			if (_maskSwapped)
			{
				(_mask, _maskSpare) = (_maskSpare, _mask); // прошлая дозапись не случилась — вернуть как было
				_maskSwapped = false;
			}

			if (bakedMask && WillUpdateMask())
			{
				(_mask, _maskSpare) = (_maskSpare, _mask);
				_maskSwapped = true;
			}

			QuadMarker.End();
			BlocksMarker.Begin();
			_quadBlock.SetVector(RectId, new Vector4(rect.x, rect.y, rect.width, rect.height));
			_quadBlock.SetVector(GridId, new Vector4(anchor.x, anchor.y, cell, _id));
			_quadBlock.SetFloat(ResolveBakedId, surfaceLattice ? 1f : 0f);
			_quadBlock.SetFloat(BakedMaskId, bakedMask ? 1f : 0f);
			_quadBlock.SetFloat(SurfaceGridId, SurfaceGrid ? 1f : 0f);

			if (bakedMask)
			{
				_quadBlock.SetTexture(BakeMaskId, _mask);
				_quadBlock.SetVector(BakeMaskRectId, _maskRect);
				_quadBlock.SetVector(BakeGridId, _bakeNew.Grid);
				float centerDepth = Vector3.Dot(center - camera.transform.position, camera.transform.forward);
				_quadBlock.SetFloat(CenterDepthId, centerDepth);
				_quadBlock.SetFloat(BakeCenterDepthId, _bakeCenterDepth);
				_quadBlock.SetFloat(SpriteScaleId, centerDepth / Mathf.Max(_bakeCenterDepth, 1e-4f));
				_maskUpdate = new Vector4(Mathf.Round(anchor.x), Mathf.Round(anchor.y), centerDepth / Mathf.Max(_bakeCenterDepth, 1e-4f),
					_bakeCenterDepth - centerDepth);
				_maskFarDepth = centerDepth - bounds.extents.magnitude; // ближайшая возможная точка объекта (см. RecordCapture)
				_maskLive = settings.LiveSilhouette;
				_maskHysteresis = settings.EdgeHysteresis;
			}
			_quadBlock.SetFloat(OutlineId, settings.Outline ? 1f : 0f);
			_quadBlock.SetColor(OutlineColorId, settings.OutlineColor);
			_quadRenderer.SetPropertyBlock(_quadBlock);
			BlocksMarker.End();
			bool skip = SurfaceGrid || ScreenGrid ? DebugSkipSurfaceResolve : DebugSkipResolve;

			if (_quadObject.activeSelf == skip)
				_quadObject.SetActive(!skip);
		}

		private bool WillUpdateMask() => _maskValid && !_capturePending && _mask != null && _maskSpare != null && !DisableMaskUpdates;

		// ---------- Запекание на поверхность ----------

		private Vector3 LocalCorner(int index)
		{
			Vector3 extents = _meshBounds.extents;
			return _meshBounds.center + new Vector3((index & 1) == 0 ? -extents.x : extents.x,
				(index & 2) == 0 ? -extents.y : extents.y, (index & 4) == 0 ? -extents.z : extents.z);
		}

		/// <summary>Угол рамки меша на экране — как Camera.WorldToScreenPoint (z — глубина), через одну матрицу объект → клип
		/// на все 8 углов (раньше — 16 вызовов движка на объект каждый кадр).</summary>
		private Vector3 CornerOnScreen(in Matrix4x4 objectToClip, int index, float width, float height)
		{
			Vector3 local = LocalCorner(index);
			Vector4 clip = objectToClip * new Vector4(local.x, local.y, local.z, 1f);
			clip.w = clip.w == 0f ? 1e-6f : clip.w;
			return new Vector3((clip.x / clip.w * 0.5f + 0.5f) * width, (clip.y / clip.w * 0.5f + 0.5f) * height, clip.w);
		}

		private Matrix4x4 ObjectToClip(Camera camera) => camera.projectionMatrix * camera.worldToCameraMatrix * transform.localToWorldMatrix;

		private void StoreCorners(Camera camera)
		{
			Matrix4x4 objectToClip = ObjectToClip(camera);

			for (int i = 0; i < 8; i++)
				_bakeCorners[i] = CornerOnScreen(objectToClip, i, camera.pixelWidth, camera.pixelHeight);
		}

		/// <summary>Насколько (в пикселях) спрайт запекания разошёлся с настоящим объектом — по углам рамки.</summary>
		private float SpriteError(Camera camera, Vector2 anchor, float depth)
		{
			float scale = depth / Mathf.Max(_bakeCenterDepth, 1e-4f);
			float error = 0f;

			for (int i = 0; i < 8; i++)
			{
				Vector3 actual = _cornersNow[i]; // посчитаны в UpdateGrid этого кадра

				if (actual.z <= camera.nearClipPlane)
					return float.MaxValue;

				Vector2 predicted = anchor + (_bakeCorners[i] - _bakeAnchor) / Mathf.Max(scale, 1e-4f);
				error = Mathf.Max(error, Vector2.Distance(predicted, actual));
			}

			return error;
		}

		/// <summary>Готовит текстуру маски под прямоугольник объекта; сам захват — после рендера источника (RecordCapture).</summary>
		private void PrepareMask(Vector2 anchor, float cell)
		{
			Vector2 from = new(Mathf.Floor((_lastRect.xMin - anchor.x) / cell) - 2f, Mathf.Floor((_lastRect.yMin - anchor.y) / cell) - 2f);
			Vector2 to = new(Mathf.Ceil((_lastRect.xMax - anchor.x) / cell) + 2f, Mathf.Ceil((_lastRect.yMax - anchor.y) / cell) + 2f);
			int width = Mathf.Clamp((int)(to.x - from.x), 1, 1024), height = Mathf.Clamp((int)(to.y - from.y), 1, 1024);

			// Без вьюпорта: тексель (i, j) — клетка (i, j) на любом API; текстура может быть больше логического размера
			// (_maskRect.zw) — лишние тексели не читаются. Растёт с запасом и не пересоздаётся при каждом перезапекании
			// (раньше — новая пара RenderTexture почти на каждое запекание: прямоугольник объекта всё время меняется).
			if (_mask == null || _mask.width < width || _mask.height < height)
			{
				int allocatedWidth = Mathf.Min(1024, Mathf.CeilToInt(Mathf.Max(width, _mask != null ? _mask.width : 0) * 1.25f / 8f) * 8);
				int allocatedHeight = Mathf.Min(1024, Mathf.CeilToInt(Mathf.Max(height, _mask != null ? _mask.height : 0) * 1.25f / 8f) * 8);

				if (_mask != null)
				{
					_mask.Release();
					DestroyImmediate(_mask);
				}

				_mask = new RenderTexture(allocatedWidth, allocatedHeight, 0, RenderTextureFormat.ARGBFloat,
					RenderTextureReadWrite.Linear)
				{
					name = name + " Silhouette Mask",
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.HideAndDontSave
				};
				_mask.Create();

				if (_maskSpare != null)
				{
					_maskSpare.Release();
					DestroyImmediate(_maskSpare);
				}

				_maskSpare = new RenderTexture(_mask.descriptor) { name = name + " Silhouette Mask Spare", hideFlags = HideFlags.HideAndDontSave };
				_maskSpare.Create();
			}

			_maskRect = new Vector4(from.x, from.y, width, height);
			_capturePending = true;
		}

		/// <summary>Записывает захват маски в командный буфер (вызывает менеджер после рендера источника).</summary>
		internal bool RecordCapture(CommandBuffer commands)
		{
			if (!_capturePending || _mask == null)
				return false;

			if (_fullscreenQuad == null)
			{
				_fullscreenQuad = new Mesh { name = "Pixel Capture Quad", hideFlags = HideFlags.HideAndDontSave };
				_fullscreenQuad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
				_fullscreenQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
				_fullscreenQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
			}

			var block = _captureBlock;
			block.Clear();
			block.SetVector(CaptureGridId, new Vector4(_bakeNew.Grid.x, _bakeNew.Grid.y, _bakeNew.Grid.z, _id));
			block.SetVector(CaptureRectId, _maskRect);
			// Порог «объект ближе нас» для клеток без наших пикселей — БЛИЖАЙШАЯ возможная точка объекта (центр − радиус),
				// а не самая дальняя: иначе пол прямо за шаром считался «перекрывающим», клетки вокруг нижней половины
				// становились «неизвестными» и сборка рисовала поверх пола лишнее кольцо клеток (шар переставал быть круглым).
				block.SetFloat(CaptureFarDepthId, _bakeCenterDepth - _renderer.bounds.extents.magnitude);
			block.SetFloat(CaptureUpdateId, 0f);
			commands.SetRenderTarget(_mask);
			commands.DrawMesh(_fullscreenQuad, Matrix4x4.identity, _resolveMaterial, 0, 1, block);
			_capturePending = false;
			_maskValid = true;
			return true;
		}

		/// <summary>
		/// Дозапись маски между запеканиями: «неизвестные» клетки (в момент запекания их закрывал другой объект),
		/// которые сейчас открылись, снимаются из текущего рендера и дальше тоже стоят на месте — без полного
		/// перезапекания. Вызывает менеджер после рендера источника.
		/// </summary>
		internal bool RecordMaskUpdate(CommandBuffer commands)
		{
			// Маски уже поменялись ролями в UpdateGrid: пишем в _mask (её и читает сборка), читаем прошлую из _maskSpare.
			if (!_maskSwapped || !_visible || _fullscreenQuad == null)
				return false;

			var block = _updateBlock;
			block.Clear();
			block.SetVector(CaptureGridId, new Vector4(_bakeNew.Grid.x, _bakeNew.Grid.y, _bakeNew.Grid.z, _id));
			block.SetVector(CaptureRectId, _maskRect);
			block.SetFloat(CaptureFarDepthId, _maskFarDepth);
			block.SetFloat(CaptureUpdateId, 1f);
			block.SetFloat(CaptureLiveId, _maskLive ? 1f : 0f);
			block.SetFloat(CaptureHysteresisId, _maskHysteresis);
			block.SetVector(CaptureCurrentId, _maskUpdate);
			block.SetTexture(BakeMaskPrevId, _maskSpare);
			commands.SetRenderTarget(_mask);
			commands.DrawMesh(_fullscreenQuad, Matrix4x4.identity, _resolveMaterial, 0, 1, block);
			_maskSwapped = false;
			return true;
		}

		/// <summary>Сетка, которая сейчас на экране при Morph: плавно перетекает из старой в новую.</summary>
		private BakedView CurrentView()
		{
			if (_bakeProgress >= 1f)
				return _bakeNew;

			float t = Mathf.SmoothStep(0f, 1f, _bakeProgress);
			return new BakedView
			{
				ObjectToClip = Lerp(_bakeOld.ObjectToClip, _bakeNew.ObjectToClip, t),
				Grid = new Vector4(
					Mathf.Lerp(_bakeOld.Grid.x, _bakeNew.Grid.x, t),
					Mathf.Lerp(_bakeOld.Grid.y, _bakeNew.Grid.y, t),
					Mathf.Lerp(_bakeOld.Grid.z, _bakeNew.Grid.z, t),
					_bakeNew.Grid.w)
			};
		}

		private static Matrix4x4 Lerp(Matrix4x4 a, Matrix4x4 b, float t)
		{
			var result = new Matrix4x4();

			for (int i = 0; i < 16; i++)
				result[i] = Mathf.Lerp(a[i], b[i], t);

			return result;
		}

		/// <summary>Запоминает ракурс заново при заметной смене направления взгляда или масштаба, ведёт растворение.</summary>
		private bool UpdateBake(Camera camera, PixelRenderSettings settings, Vector2 anchor, float cell)
		{
			if (settings.AutoFromTexels)
				return false;

			Vector3 center = _renderer.bounds.center;
			Vector3 toCamera = camera.transform.position - center;
			float depth = Vector3.Dot(toCamera, -camera.transform.forward);
			float focal = 0.5f * camera.pixelHeight / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
			float worldCell = cell * Mathf.Max(depth, camera.nearClipPlane) / focal;
			Vector3 direction = transform.InverseTransformDirection(toCamera.normalized);
			Vector3 up = transform.InverseTransformDirection(camera.transform.up);

			// Ошибка спрайта в пикселях экрана, честно: углы рамки меша, перенесённые как спрайт (сдвиг за якорем +
			// масштаб по глубине), против их настоящего положения сейчас. Ловит и масштаб, и поворот, и перспективные
			// искажения у края кадра.
			bool byError = _baked && settings.BakeMaxError > 0f && SpriteError(camera, anchor, depth) > settings.BakeMaxError * cell;
			bool byThresholds = _baked && (Vector3.Angle(direction, _bakeDirection) > settings.BakeAngle
			                               || Vector3.Angle(up, _bakeUp) > settings.BakeAngle
			                               || Mathf.Abs(Mathf.Log(worldCell / Mathf.Max(_bakeWorldCell, 1e-6f))) > Mathf.Log(1f + settings.BakeScale));
			bool rebake = !_baked || byError || byThresholds;

			if (byError) BakesByError++;
			else if (byThresholds) BakesByThresholds++;

			if (rebake)
			{
				var view = new BakedView
				{
					ObjectToClip = camera.projectionMatrix * camera.worldToCameraMatrix * transform.localToWorldMatrix,
					Grid = new Vector4(anchor.x, anchor.y, cell, _bakeCount % 1000)
				};

				// Переход начинается с того, что сейчас на экране (при Morph — с промежуточной сетки), без скачка.
				_bakeOld = !_baked ? view : settings.Transition == BakeTransition.Morph ? CurrentView() : _bakeNew;
				_bakeNew = view;
				_bakeProgress = !_baked || settings.BakeDissolve <= 0f ? 1f : 0f;
				_bakeDirection = direction;
				_bakeUp = up;
				_bakeWorldCell = worldCell;
				_bakeCenterDepth = depth;
				_bakeAnchor = anchor;
				StoreCorners(camera);
				_baked = true;
				_bakeCount++;

				if (settings.BakeSilhouette)
					PrepareMask(anchor, cell);


			}
			else if (_bakeProgress < 1f)
			{
				float delta = Application.isPlaying ? Time.deltaTime : 1f / 60f;
				_bakeProgress = Mathf.Min(1f, _bakeProgress + delta / Mathf.Max(settings.BakeDissolve, 1e-3f));
			}

			return true;
		}

		private float EffectivePixelWorldSize(int pixelsPerTexel)
		{
			var material = _renderer.sharedMaterial;
			Vector2 checks = material != null && material.HasProperty(ChecksId) ? (Vector2)material.GetVector(ChecksId) : new Vector2(8f, 8f);
			float texelsPerUv = Mathf.Max(checks.x, checks.y, 1f);
			Vector3 scale = transform.lossyScale;
			float uniformScale = (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;
			float texelWorld = _metersPerUv * uniformScale / texelsPerUv;
			return Mathf.Max(0.005f, texelWorld / Mathf.Max(1, pixelsPerTexel));
		}

		/// <summary>Средняя плотность UV меша: сколько метров на единицу UV.</summary>
		private static float MeasureMetersPerUv(Mesh mesh)
		{
			if (mesh == null)
				return 1f;

			var vertices = mesh.vertices;
			var uvs = mesh.uv;
			var triangles = mesh.triangles;

			if (uvs == null || uvs.Length != vertices.Length)
				return 1f;

			double worldArea = 0, uvArea = 0;

			for (int i = 0; i < triangles.Length; i += 3)
			{
				int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
				worldArea += Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude * 0.5;
				Vector2 uvB = uvs[b] - uvs[a], uvC = uvs[c] - uvs[a];
				uvArea += Mathf.Abs(uvB.x * uvC.y - uvB.y * uvC.x) * 0.5;
			}

			return uvArea > 1e-8 ? (float)System.Math.Sqrt(worldArea / uvArea) : 1f;
		}
	}
}
