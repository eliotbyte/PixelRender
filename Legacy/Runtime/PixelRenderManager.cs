using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EliotByte.PixelRender.Legacy
{
	/// <summary>
	/// Пиксельный рендер без импостеров. Вешается на основную камеру.
	///
	/// 1. Камера-источник (та же поза и проекция, что у основной) рисует все пиксельные объекты (слой Layer)
	///    одним проходом в полноразмерную текстуру ARGBFloat: цвет + id объекта + глубина
	///    (шейдер EliotByte/PixelRender/Source).
	/// 2. Каждый PixelRenderObject выводит квад по своему прямоугольнику на экране (EliotByte/PixelRender/Resolve),
	///    который собирает объект крупными пикселями по его собственной сетке.
	///
	/// Никаких отдельных камер и текстур на объект — всё за два прохода на любое число объектов.
	/// </summary>
	[DefaultExecutionOrder(1000)]
	[RequireComponent(typeof(Camera))]
	public class PixelRenderManager : MonoBehaviour
	{
		public const int Layer = 31;

		// Метки для профайлера (Profiler / ProfilerRecorder).
		private static readonly ProfilerMarker StepMarker = new("PixelRender.Step");
		private static readonly ProfilerMarker GridsMarker = new("PixelRender.UpdateGrids");
		private static readonly ProfilerMarker CapturesMarker = new("PixelRender.RecordCaptures");

		private static readonly int SourceId = Shader.PropertyToID("_PixelSource");
		private static readonly int SourceSizeId = Shader.PropertyToID("_PixelSourceSize");
		private static readonly int SourcePassId = Shader.PropertyToID("_PixelSourcePass");
		private static readonly int ScreenMatrixId = Shader.PropertyToID("_PixelScreenMatrix");
		private static readonly int HistoryId = Shader.PropertyToID("_PixelHistory");
		private static readonly int PrevScreenMatrixId = Shader.PropertyToID("_PixelPrevScreenMatrix");
		private static readonly int HistoryParamsId = Shader.PropertyToID("_PixelHistoryParams");
		private static readonly int SurfaceBakeId = Shader.PropertyToID("_PixelSurfaceBake");
		private static readonly int SurfaceBakeInverseId = Shader.PropertyToID("_PixelSurfaceBakeInverse");
		private static readonly int SurfaceCellId = Shader.PropertyToID("_PixelSurfaceCell");
		private static readonly int ScreenInverseId = Shader.PropertyToID("_PixelScreenInverse");
		private static readonly int CameraPositionId = Shader.PropertyToID("_PixelCameraPosition");
		private static readonly int CameraForwardId = Shader.PropertyToID("_PixelCameraForward");
		private CommandBuffer _captureCommands;

		// Запечённая экранная сетка поверхностей: поза камеры и клетка на момент запекания.
		private Matrix4x4 _surfaceBake;
		private Vector3 _surfaceRight, _surfaceUp, _surfacePosition;
		private float _surfaceCell;
		private bool _surfaceBaked;
		private bool _surfaceCapturePending;
		private RenderTexture _surfaceMask;
		private Vector4 _surfaceMaskRect;
		private Material _surfaceCaptureMaterial;
		private Mesh _fullscreenQuad;
		private const float SurfaceMaskMargin = 0.25f; // запас маски за краями экрана (доля экрана с каждой стороны)
		private static readonly int SurfaceMaskId = Shader.PropertyToID("_PixelSurfaceMask");
		private static readonly int SurfaceMaskRectId = Shader.PropertyToID("_PixelSurfaceMaskRect");

		/// <summary>Сколько раз перезапекалась сетка поверхностей (для тестов).</summary>
		public static int SurfaceBakes;

		internal static readonly List<PixelRenderObject> Objects = new();
		private static readonly Stack<int> FreeIds = new();
		private static int _nextId = 1;

		private Camera _camera;
		private Camera _source;
		private RenderTexture _target;
		private RenderTexture _history;
		private bool _historyValid;
		private Matrix4x4 _previousScreenMatrix;

		[Tooltip("Общие настройки всех пиксельных объектов сцены (кроме тех, у кого включено Own Settings).")]
		public PixelRenderSettings Settings = new();

		[Header("Экспериментальный экранный эффект")]
		[Tooltip("Упрощённая пикселизация всего кадра. Выключите галочку, чтобы отключить эффект.")]
		public bool ScreenPixelMajority = false;

		[Header("Стабильность")]
		[Tooltip("Гистерезис (как у импостера): если клетка спорная — доля тёмного в ней близка к половине, — " +
		         "она берёт решение той же точки поверхности из прошлого кадра. Иначе такие клетки мигают при движении.")]
		public bool Hysteresis = true;
		[Tooltip("Насколько доля тёмного должна уйти от 0.5, чтобы клетка сменила цвет вопреки прошлому кадру.")]
		[Range(0f, 0.5f)] public float HysteresisBand = 0.3f;

		[Header("Сетка на поверхности")]
		[Tooltip("Объекты с Surface Grid берут экранную сетку камеры в момент запекания (квадратные клетки Screen Cell Pixels) " +
		         "и держат её приклеенной к поверхности. Перезапекание — когда камера повернулась на столько градусов.")]
		[Range(0.5f, 45f)] public float SurfaceGridAngle = 6f;
		[Tooltip("Перезапекание, когда камера сдвинулась на такую долю расстояния до ближайшей поверхности с Surface Grid — " +
		         "примерно настолько исказилась ближняя клетка (0.15 — на 15%). Меньше — клетки ровнее, но обновляются чаще.")]
		[Range(0.01f, 1f)] public float SurfaceGridDistortion = 0.15f;

		public Camera SourceCamera => _source;
		public RenderTexture SourceTexture => _target;

		// Номера переиспользуются: id хранится в float вместе с цветом, большие числа съели бы точность.
		internal static int TakeId() => FreeIds.Count > 0 ? FreeIds.Pop() : _nextId++;

		internal static void ReturnId(int id)
		{
			if (id > 0)
				FreeIds.Push(id);
		}

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

		public void Initialize()
		{
			if (_source != null)
				return;

			_camera = GetComponent<Camera>();
			_camera.cullingMask &= ~(1 << Layer);

			var sourceObject = new GameObject("Pixel Source Camera") { hideFlags = HideFlags.HideAndDontSave };
			_source = sourceObject.AddComponent<Camera>();
			_source.cullingMask = 1 << Layer;
			_source.clearFlags = CameraClearFlags.SolidColor;
			_source.backgroundColor = new Color(0f, 0f, 0f, 0f);
			_source.allowMSAA = false;
			_source.allowHDR = false;
			_source.useOcclusionCulling = false;

			var data = _source.GetUniversalAdditionalCameraData();
			data.renderPostProcessing = false;
			data.antialiasing = AntialiasingMode.None;
			data.requiresColorOption = CameraOverrideOption.Off;
			data.requiresDepthOption = CameraOverrideOption.Off;
		}

		public void Release()
		{
			_captureCommands?.Release();
			_captureCommands = null;

			if (_source != null)
			{
				_source.targetTexture = null;

				if (Application.isPlaying)
					Destroy(_source.gameObject);
				else
					DestroyImmediate(_source.gameObject);

				_source = null;
			}

			ReleaseTexture(ref _target);
			ReleaseTexture(ref _history);
			ReleaseTexture(ref _surfaceMask);
			_historyValid = false;
			_surfaceBaked = false;
			_surfaceCapturePending = false;

			if (_surfaceCaptureMaterial != null)
			{
				if (Application.isPlaying) Destroy(_surfaceCaptureMaterial); else DestroyImmediate(_surfaceCaptureMaterial);
				_surfaceCaptureMaterial = null;
			}

			if (_fullscreenQuad != null)
			{
				if (Application.isPlaying) Destroy(_fullscreenQuad); else DestroyImmediate(_fullscreenQuad);
				_fullscreenQuad = null;
			}
		}

		private static void ReleaseTexture(ref RenderTexture texture)
		{
			if (texture == null)
				return;

			texture.Release();

			if (Application.isPlaying)
				Destroy(texture);
			else
				DestroyImmediate(texture);

			texture = null;
		}

		private static RenderTexture CreateSourceTexture(int width, int height, string name)
		{
			var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
			{
				name = name,
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave
			};
			texture.Create();
			return texture;
		}

		private void LateUpdate() => Step();

		/// <summary>Синхронизирует камеру-источник с основной и обновляет сетки всех объектов.</summary>
		public void Step()
		{
			using var stepScope = StepMarker.Auto();
			Initialize();
			_camera.cullingMask &= ~(1 << Layer);
			int width = Mathf.Max(1, _camera.pixelWidth);
			int height = Mathf.Max(1, _camera.pixelHeight);

			if (_target == null || _target.width != width || _target.height != height)
			{
				ReleaseTexture(ref _target);
				ReleaseTexture(ref _history);
				_target = CreateSourceTexture(width, height, "Pixel Source A");
				_history = CreateSourceTexture(width, height, "Pixel Source B");
				_historyValid = false;
			}
			else
			{
				// Прошлый кадр источника становится историей, рисуем в другую текстуру.
				(_target, _history) = (_history, _target);
			}

			_source.transform.SetPositionAndRotation(_camera.transform.position, _camera.transform.rotation);
			_source.fieldOfView = _camera.fieldOfView;
			_source.nearClipPlane = _camera.nearClipPlane;
			_source.farClipPlane = _camera.farClipPlane;
			_source.depth = _camera.depth - 1f;
			_source.targetTexture = _target;
			_source.aspect = width / (float)height;

			Shader.SetGlobalTexture(SourceId, _target);
			Shader.SetGlobalVector(SourceSizeId, new Vector4(width, height, 1f / width, 1f / height));
			// Мир → экран ровно как у Camera.WorldToScreenPoint (по нему считаются якоря сеток): источник считает
			// цвет в точном центре клетки, а не в пикселе, который в неё попал.
			Matrix4x4 screenMatrix = _camera.projectionMatrix * _camera.worldToCameraMatrix;
			Shader.SetGlobalMatrix(ScreenMatrixId, screenMatrix);
			UpdateSurfaceBake(screenMatrix, height);
			Shader.SetGlobalTexture(HistoryId, _history);
			Shader.SetGlobalMatrix(PrevScreenMatrixId, _historyValid ? _previousScreenMatrix : screenMatrix);
			Shader.SetGlobalVector(HistoryParamsId, new Vector4(Hysteresis && _historyValid ? 1f : 0f, HysteresisBand, 0.05f, 0f));
			_previousScreenMatrix = screenMatrix;
			_historyValid = true;

			using var gridsScope = GridsMarker.Auto();

			for (int i = Objects.Count - 1; i >= 0; i--)
			{
				if (Objects[i] == null)
					Objects.RemoveAt(i);
				else
					Objects[i].UpdateGrid(_camera, Settings);
			}
		}

		/// <summary>
		/// Запекание экранной сетки поверхностей: запоминаем мир → клип камеры, когда она повернулась больше
		/// Surface Grid Angle или сдвинулась больше Surface Grid Distortion от расстояния до ближайшей поверхности.
		/// </summary>
		private void UpdateSurfaceBake(Matrix4x4 screenMatrix, int height)
		{
			Transform cameraTransform = _camera.transform;
			Vector3 position = cameraTransform.position;
			float cell = Mathf.Max(1f, Settings.ScreenCellPixels * Settings.ScreenScale(height));
			bool rebake = !_surfaceBaked || !Mathf.Approximately(cell, _surfaceCell)
			              || Vector3.Angle(cameraTransform.right, _surfaceRight) > SurfaceGridAngle
			              || Vector3.Angle(cameraTransform.up, _surfaceUp) > SurfaceGridAngle;

			if (!rebake)
			{
				float nearest = float.MaxValue;

				foreach (var pixelObject in Objects)
				{
					if (pixelObject != null && pixelObject.SurfaceGrid && pixelObject.isActiveAndEnabled)
					{
						Bounds bounds = pixelObject.GetComponent<Renderer>().bounds;
						nearest = Mathf.Min(nearest, Vector3.Distance(bounds.ClosestPoint(position), position));
					}
				}

				nearest = Mathf.Max(nearest, _camera.nearClipPlane * 2f);
				rebake = nearest < float.MaxValue && Vector3.Distance(position, _surfacePosition) > SurfaceGridDistortion * nearest;
			}

			if (rebake)
			{
				_surfaceBake = screenMatrix;
				_surfaceRight = cameraTransform.right;
				_surfaceUp = cameraTransform.up;
				_surfacePosition = position;
				_surfaceCell = cell;
				_surfaceBaked = true;
				_surfaceCapturePending = true;
				SurfaceBakes++;
				PrepareSurfaceMask(cell);
			}

			Shader.SetGlobalTexture(SurfaceMaskId, _surfaceMask != null ? _surfaceMask : Texture2D.blackTexture);
			Shader.SetGlobalVector(SurfaceMaskRectId, _surfaceMask != null && !_surfaceCapturePending ? _surfaceMaskRect : Vector4.zero);

			Shader.SetGlobalMatrix(SurfaceBakeId, _surfaceBake);
			Shader.SetGlobalMatrix(SurfaceBakeInverseId, _surfaceBake.inverse);
			Shader.SetGlobalFloat(SurfaceCellId, _surfaceCell);
			Shader.SetGlobalMatrix(ScreenInverseId, screenMatrix.inverse);
			Shader.SetGlobalVector(CameraPositionId, position);
			Shader.SetGlobalVector(CameraForwardId, cameraTransform.forward);
		}

		private void PrepareSurfaceMask(float cell)
		{
			int offsetX = Mathf.CeilToInt(_target.width / cell * SurfaceMaskMargin);
			int offsetY = Mathf.CeilToInt(_target.height / cell * SurfaceMaskMargin);
			int width = Mathf.CeilToInt(_target.width / cell) + offsetX * 2;
			int height = Mathf.CeilToInt(_target.height / cell) + offsetY * 2;

			if (_surfaceMask == null || _surfaceMask.width != width || _surfaceMask.height != height)
			{
				ReleaseTexture(ref _surfaceMask);
				_surfaceMask = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
				{
					name = "Pixel Surface Mask",
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.HideAndDontSave
				};
				_surfaceMask.Create();
			}

			_surfaceMaskRect = new Vector4(offsetX, offsetY, width, height);
		}

		/// <summary>Снимок маски запекания сетки поверхностей — из только что нарисованного источника.</summary>
		private bool RecordSurfaceCapture(CommandBuffer commands)
		{
			if (!_surfaceCapturePending || _surfaceMask == null || SuppressCaptures)
				return false;

			if (_surfaceCaptureMaterial == null)
				_surfaceCaptureMaterial = new Material(Shader.Find("EliotByte/PixelRender/Legacy/Resolve")) { hideFlags = HideFlags.HideAndDontSave };

			if (_fullscreenQuad == null)
			{
				_fullscreenQuad = new Mesh { name = "Pixel Surface Capture Quad", hideFlags = HideFlags.HideAndDontSave };
				_fullscreenQuad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
				_fullscreenQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
				_fullscreenQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
			}

			commands.SetGlobalVector(SurfaceMaskRectId, _surfaceMaskRect);
			commands.SetGlobalFloat(SurfaceCellId, _surfaceCell);
			commands.SetRenderTarget(_surfaceMask);
			commands.DrawMesh(_fullscreenQuad, Matrix4x4.identity, _surfaceCaptureMaterial, 0, 2);
			_surfaceCapturePending = false;
			return true;
		}

		/// <summary>Для редактора и тестов: шаг + рендер источника прямо сейчас.</summary>
		public void RenderSourceNow()
		{
			Step();
			Shader.SetGlobalFloat(SourcePassId, 1f);
			_source.enabled = false;
			RenderPipeline.SubmitRenderRequest(_source, new RenderPipeline.StandardRequest { destination = _target });
			Shader.SetGlobalFloat(SourcePassId, 0f);

			_captureCommands ??= new CommandBuffer { name = "Pixel Silhouette Capture" };
			_captureCommands.Clear();

			if (RecordCaptures(_captureCommands))
				Graphics.ExecuteCommandBuffer(_captureCommands);
		}

		/// <summary>Маски силуэтов объектов, запечённых в этом кадре, — из только что нарисованного источника.</summary>
		/// <summary>Для тестов: не снимать и не дозаписывать маски (служебные рендеры эталона).</summary>
		public static bool SuppressCaptures;

		private bool RecordCaptures(CommandBuffer commands)
		{
			using var capturesScope = CapturesMarker.Auto();
			if (SuppressCaptures)
				return false;

			bool any = false;
			commands.SetGlobalTexture(SourceId, _target);
			any |= RecordSurfaceCapture(commands);

			foreach (var pixelObject in Objects)
			{
				if (pixelObject == null)
					continue;

				any |= pixelObject.RecordMaskUpdate(commands);
				any |= pixelObject.RecordCapture(commands);
			}

			return any;
		}

		// Флаг «сейчас рисует источник» ставится командой в очереди этой камеры — тогда он точно действует
		// ровно на её отрисовку, а Scene view и основная камера видят обычный цвет.
		private void OnBeginCamera(ScriptableRenderContext context, Camera camera) => SetSourcePass(context, camera == _source);

		private void OnEndCamera(ScriptableRenderContext context, Camera camera)
		{
			if (camera != _source)
				return;

			SetSourcePass(context, false);
			var commands = CommandBufferPool.Get("Pixel Silhouette Capture");

			if (RecordCaptures(commands))
				context.ExecuteCommandBuffer(commands);

			CommandBufferPool.Release(commands);
		}

		private static void SetSourcePass(ScriptableRenderContext context, bool value)
		{
			var command = CommandBufferPool.Get("Pixel Source Flag");
			command.SetGlobalFloat(SourcePassId, value ? 1f : 0f);
			context.ExecuteCommandBuffer(command);
			CommandBufferPool.Release(command);
			Shader.SetGlobalFloat(SourcePassId, value ? 1f : 0f);
		}
	}
}
