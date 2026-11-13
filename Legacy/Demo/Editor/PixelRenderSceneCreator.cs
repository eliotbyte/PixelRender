using System.IO;
using System.Text;
using EliotByte.PixelRender.Legacy;
using EliotByte.PixelRender.Legacy.Demo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace EliotByte.PixelRender.Legacy.Demo.Editor
{
	/// <summary>
	/// Сцена PixelRender (пиксельный рендер без импостеров): шахматный шар в центре, колонны, шахматный пол,
	/// с неба равномерно по всему полу падают 300 разноцветных капсул; камера сама медленно облетает шар.
	/// Меню: Tools/Pixel Render/Create Pixel Render Scene, Tools/Pixel Render/Capture Pixel Render Scene.
	/// Файлы-запросы в корне проекта: PixelRenderScene.request (создать заново + снимок),
	/// PixelRenderCapture.request (только снимок) → SandboxCaptures/PixelRender/.
	/// Если в открытой сцене есть несохранённые изменения, ничего не делает (пишет об этом в report.txt).
	/// </summary>
	[InitializeOnLoad]
	public static class PixelRenderSceneCreator
	{
		public const string ScenePath = "Assets/External/PixelRender/Legacy/Demo/Scenes/PixelRender.unity";
		public const string FloorTestScenePath = "Assets/External/PixelRender/Legacy/Demo/Scenes/PixelRenderFloorTest.unity";
		private const string MaterialFolder = "Assets/External/PixelRender/Legacy/Demo/Materials";
		private const string MeshFolder = "Assets/External/PixelRender/Legacy/Demo/Meshes";
		private const string CreateRequest = "PixelRenderScene.request";
		private const string CaptureRequest = "PixelRenderCapture.request";
		// Содержимое — размер пола в метрах (например «20»): пересоздаёт только меш пола, сцену не трогает.
		private const string FloorRequest = "PixelRenderFloor.request";
		private const string OutputFolder = "SandboxCaptures/PixelRender";
		private const float ColumnHeight = 7f * 2f / 3f;

		private static double _nextPoll;

		static PixelRenderSceneCreator()
		{
			EditorApplication.update -= Poll;
			EditorApplication.update += Poll;
		}

		private static void Poll()
		{
			if (EditorApplication.timeSinceStartup < _nextPoll)
				return;

			_nextPoll = EditorApplication.timeSinceStartup + 1.0;

			if (File.Exists(FloorRequest) && !EditorApplication.isCompiling && !EditorApplication.isUpdating
			    && !EditorApplication.isPlayingOrWillChangePlaymode)
			{
				string text = File.ReadAllText(FloorRequest).Trim();
				File.Delete(FloorRequest);

				if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size) && size > 0f)
					ResizeFloor(size);
			}

			bool create = File.Exists(CreateRequest);
			bool capture = File.Exists(CaptureRequest);

			if (!create && !capture)
				return;

			if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
				return;

			AssetDatabase.Refresh();

			if (EditorApplication.isCompiling || EditorApplication.isUpdating)
				return;

			if (create)
				File.Delete(CreateRequest);

			if (capture)
				File.Delete(CaptureRequest);

			Directory.CreateDirectory(OutputFolder);

			if (SceneManager.GetActiveScene().isDirty)
			{
				File.WriteAllText(Path.Combine(OutputFolder, "report.txt"),
					"SKIPPED: в открытой сцене есть несохранённые изменения — сохрани её, и запрос повторится.");
				return;
			}

			string previous = SceneManager.GetActiveScene().path;

			if (create || !File.Exists(ScenePath))
				CreateScene();

			Capture();

			if (!string.IsNullOrEmpty(previous) && previous != ScenePath)
				EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
		}

		[MenuItem("Tools/Pixel Render/Legacy/Create Pixel Render Scene")]
		public static void CreateScene()
		{
			Directory.CreateDirectory(MaterialFolder);
			Directory.CreateDirectory(MeshFolder);
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			// Цвета — с примера: чёрно-белый шар, кремово-чёрные колонны, тёмно-серый пол, яркие сферы.
			var ball = CreateMaterial("Ball", new Color(0.96f, 0.96f, 0.94f), new Color(0.04f, 0.04f, 0.05f), new Vector2(16f, 8f));
			var column = CreateMaterial("Column", new Color(0.93f, 0.89f, 0.80f), new Color(0.10f, 0.10f, 0.11f), new Vector2(3f, 3f));
			var floor = CreateMaterial("Floor", new Color(0.30f, 0.30f, 0.32f), new Color(0.24f, 0.24f, 0.26f), new Vector2(0.5f, 0.5f));
			var palette = new[]
			{
				CreateMaterial("Orange", new Color(1.00f, 0.58f, 0.08f), new Color(0.12f, 0.08f, 0.05f), new Vector2(8f, 6f)),
				CreateMaterial("RedYellow", new Color(0.92f, 0.16f, 0.10f), new Color(1.00f, 0.84f, 0.18f), new Vector2(8f, 6f)),
				CreateMaterial("Blue", new Color(0.14f, 0.38f, 0.95f), new Color(0.45f, 0.85f, 1.00f), new Vector2(8f, 6f)),
				CreateMaterial("Green", new Color(0.30f, 0.82f, 0.22f), new Color(0.08f, 0.42f, 0.10f), new Vector2(8f, 6f)),
				CreateMaterial("Pink", new Color(0.96f, 0.42f, 0.86f), new Color(0.50f, 0.20f, 0.76f), new Vector2(8f, 6f))
			};

			// Свет.
			var lightObject = new GameObject("Directional Light");
			var light = lightObject.AddComponent<Light>();
			light.type = LightType.Directional;
			light.intensity = 1.1f;
			light.color = new Color(1f, 0.97f, 0.92f);
			light.shadows = LightShadows.None; // тени пока выключены
			lightObject.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = new Color(0.42f, 0.44f, 0.50f);
			RenderSettings.ambientEquatorColor = new Color(0.30f, 0.30f, 0.33f);
			RenderSettings.ambientGroundColor = new Color(0.16f, 0.16f, 0.18f);
			RenderSettings.fog = false;

			// Камера.
			var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
			var camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = Color.black;
			camera.fieldOfView = 50f;
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = 200f;
			var motion = cameraObject.AddComponent<PixelRenderCameraMotion>();
			motion.Apply(0f);
			cameraObject.AddComponent<PixelRenderManager>();

			// Пол — экранная сетка.
			var floorMesh = SaveMesh(PixelRenderMeshes.Box(new Vector3(20f, 0.4f, 20f), "PixelRender Floor"));
			var floorObject = CreatePixelObject("Floor", floorMesh, floor, new Vector3(0f, -0.2f, 0f), Quaternion.identity, Vector3.one);
			// Толстый коллайдер (верх — на уровне пола), чтобы капсулы с высоты его не проскакивали.
			var floorCollider = floorObject.AddComponent<BoxCollider>();
			floorCollider.size = new Vector3(20f, 4f, 20f);
			floorCollider.center = new Vector3(0f, -1.8f, 0f);
			var floorPixel = floorObject.GetComponent<PixelRenderObject>();
			floorPixel.SurfaceGrid = true;
			floorPixel.ScreenCellPixels = 7f;

			// Шар в центре.
			var sphereMesh = PrimitiveMesh(PrimitiveType.Sphere);
			var ballObject = CreatePixelObject("Checker Ball", sphereMesh, ball, new Vector3(0f, 2.2f, 0f), Quaternion.Euler(0f, 20f, 0f), Vector3.one * 4.4f);
			ballObject.AddComponent<SphereCollider>();

			// Колонны.
			var columnMesh = SaveMesh(PixelRenderMeshes.Box(new Vector3(1f, ColumnHeight, 1f), "PixelRender Column"));
			var columnPlaces = new[]
			{
				(new Vector3(-4.5f, ColumnHeight * 0.5f, 2.25f), Quaternion.Euler(0f, 15f, 0f)),
				(new Vector3(4.125f, ColumnHeight * 0.5f, 3.75f), Quaternion.Euler(0f, 40f, 0f)),
				(new Vector3(5.25f, ColumnHeight * 0.5f, -3f), Quaternion.Euler(0f, -10f, 0f)),
				(new Vector3(-3.75f, ColumnHeight * 0.5f, -4.5f), Quaternion.Euler(0f, 30f, 0f))
			};

			for (int i = 0; i < columnPlaces.Length; i++)
			{
				var columnObject = CreatePixelObject($"Column {i + 1}", columnMesh, column, columnPlaces[i].Item1, columnPlaces[i].Item2, Vector3.one);
				columnObject.AddComponent<BoxCollider>();
			}

			// Капсулы падают с неба (спавнер в Play).
			var spawnerObject = new GameObject("Capsule Rain");
			var spawner = spawnerObject.AddComponent<PixelRainSpawner>();
			spawner.Materials = palette;
			spawner.HalfExtent = 9.6f; // пол 20×20
			spawner.WallHalfExtent = 10f;

			EditorSceneManager.SaveScene(scene, ScenePath);
			AssetDatabase.SaveAssets();
		}

		/// <summary>
		/// Тест пола: белый пол (20×20, сетка на поверхности), оранжевый куб на нём, чёрный фон, без света,
		/// камера как в демо. На ней видно и дрожание силуэта пола, и дырки там, где пол касается объекта.
		/// Тест движения на ней: метка с «floortest».
		/// </summary>
		[MenuItem("Tools/Pixel Render/Legacy/Create Floor Test Scene")]
		public static void CreateFloorTestScene()
		{
			Directory.CreateDirectory(MaterialFolder);
			Directory.CreateDirectory(MeshFolder);
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var white = CreateMaterial("TestWhite", Color.white, Color.white, new Vector2(1f, 1f));
			var orange = CreateMaterial("TestOrange", new Color(1f, 0.55f, 0.08f), new Color(1f, 0.55f, 0.08f), new Vector2(1f, 1f));
			RenderSettings.fog = false;

			var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
			var camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = Color.black;
			camera.fieldOfView = 50f;
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = 200f;
			var motion = cameraObject.AddComponent<PixelRenderCameraMotion>();
			motion.Apply(0f);
			cameraObject.AddComponent<PixelRenderManager>();

			var floorMesh = SaveMesh(PixelRenderMeshes.Box(new Vector3(20f, 0.4f, 20f), "PixelRender Floor"));
			var floorObject = CreatePixelObject("Floor", floorMesh, white, new Vector3(0f, -0.2f, 0f), Quaternion.identity, Vector3.one);
			floorObject.GetComponent<PixelRenderObject>().SurfaceGrid = true;

			var cubeMesh = SaveMesh(PixelRenderMeshes.Box(new Vector3(1.5f, 1.5f, 1.5f), "PixelRender Test Cube"));
			CreatePixelObject("Cube", cubeMesh, orange, new Vector3(0f, 0.75f, 0f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);

			EditorSceneManager.SaveScene(scene, FloorTestScenePath);
			AssetDatabase.SaveAssets();
		}

		/// <summary>
		/// Размер пола прямо в открытой сцене (без пересборки): меш пола, его коллайдер, площадь падения капсул и стены.
		/// Остальное в сцене не трогается; сцена помечается изменённой — её нужно сохранить.
		/// </summary>
		public static void ResizeFloor(float size)
		{
			Directory.CreateDirectory(MeshFolder);
			var mesh = SaveMesh(PixelRenderMeshes.Box(new Vector3(size, 0.4f, size), "PixelRender Floor"));
			AssetDatabase.SaveAssets();
			AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(mesh), ImportAssetOptions.ForceUpdate);
			mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GetAssetPath(mesh));

			var report = new StringBuilder($"floor {size}: ");

			foreach (var pixelObject in Object.FindObjectsByType<PixelRenderObject>(FindObjectsSortMode.None))
			{
				if (pixelObject.name != "Floor")
					continue;

				var filter = pixelObject.GetComponent<MeshFilter>();
				Undo.RecordObject(filter, "Resize floor");
				filter.sharedMesh = null;
				filter.sharedMesh = mesh;

				var collider = pixelObject.GetComponent<BoxCollider>();

				if (collider != null)
				{
					Undo.RecordObject(collider, "Resize floor");
					collider.size = new Vector3(size, 4f, size);
					collider.center = new Vector3(0f, -1.8f, 0f);
				}

				report.Append("floor ok; ");
			}

			foreach (var spawner in Object.FindObjectsByType<PixelRainSpawner>(FindObjectsSortMode.None))
			{
				Undo.RecordObject(spawner, "Resize floor");
				spawner.HalfExtent = size * 0.5f - 0.4f;
				spawner.WallHalfExtent = size * 0.5f;
				report.Append("spawner ok; ");
			}

			EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
			Directory.CreateDirectory(OutputFolder);
			File.WriteAllText(Path.Combine(OutputFolder, "floor.txt"), report.ToString());
		}

		[MenuItem("Tools/Pixel Render/Legacy/Capture Pixel Render Scene")]
		public static void CaptureMenu()
		{
			if (SceneManager.GetActiveScene().path != ScenePath)
				EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

			Capture();
		}

		/// <summary>
		/// Снимок в редакторе: источник и итог 1280×720 → SandboxCaptures/PixelRender/
		/// (main.png — итог, source.png — источник в полном разрешении, report.txt — ошибки шейдеров и т. п.).
		/// </summary>
		private static void Capture()
		{
			if (SceneManager.GetActiveScene().path != ScenePath)
				EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

			var report = new StringBuilder();

			foreach (string shaderName in new[] { "EliotByte/PixelRender/Legacy/Source", "EliotByte/PixelRender/Legacy/Resolve" })
			{
				var shader = Shader.Find(shaderName);

				if (shader == null)
				{
					report.AppendLine($"{shaderName}: НЕ НАЙДЕН");
					continue;
				}

				foreach (var message in ShaderUtil.GetShaderMessages(shader))
					report.AppendLine($"{shaderName}: {message.severity} line {message.line}: {message.message}");
			}

			var manager = Object.FindFirstObjectByType<PixelRenderManager>();
			var camera = manager != null ? manager.GetComponent<Camera>() : null;

			if (camera == null)
			{
				report.AppendLine("Нет PixelRenderManager на камере.");
				File.WriteAllText(Path.Combine(OutputFolder, "report.txt"), report.ToString());
				return;
			}

			var screen = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };

			// Для снимка — временные капсулы на разной высоте (в Play их создаёт спавнер).
			var previewCapsules = new System.Collections.Generic.List<GameObject>();
			var spawner = Object.FindFirstObjectByType<PixelRainSpawner>();

			if (spawner != null)
			{
				var random = new System.Random(3);

				for (int i = 0; i < 60; i++)
				{
					var capsule = spawner.Spawn(0.4f + (float)random.NextDouble() * 12f);

					if (capsule != null)
						previewCapsules.Add(capsule);
				}
			}

			var objects = Object.FindObjectsByType<PixelRenderObject>(FindObjectsSortMode.None);
			camera.targetTexture = screen;

			try
			{
				camera.GetComponent<PixelRenderCameraMotion>()?.Apply(0f);
				manager.Initialize();

				foreach (var pixelObject in objects)
					pixelObject.Initialize();

				manager.RenderSourceNow();
				RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = screen });

				SaveTexture(screen, Path.Combine(OutputFolder, "main.png"), false);
				SaveTexture(manager.SourceTexture, Path.Combine(OutputFolder, "source.png"), true);
				report.AppendLine($"objects: {objects.Length}, source {manager.SourceTexture.width}x{manager.SourceTexture.height}");
			}
			finally
			{
				foreach (var pixelObject in objects)
					pixelObject.Release();

				foreach (var capsule in previewCapsules)
					Object.DestroyImmediate(capsule);

				manager.Release();
				camera.targetTexture = null;
				screen.Release();
				Object.DestroyImmediate(screen);
				File.WriteAllText(Path.Combine(OutputFolder, "report.txt"), report.ToString());
			}
		}

		private static void SaveTexture(RenderTexture texture, string path, bool decodeSource)
		{
			var previous = RenderTexture.active;
			RenderTexture.active = texture;
			var copy = new Texture2D(texture.width, texture.height, decodeSource ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, decodeSource);
			copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
			copy.Apply();
			RenderTexture.active = previous;

			if (decodeSource)
			{
				// Источник: b содержит id*4 — убираем, цвет переводим в гамму для просмотра.
				var pixels = copy.GetPixels();

				for (int i = 0; i < pixels.Length; i++)
				{
					Color c = pixels[i];
					float id = Mathf.Floor(c.b / 4f + 1e-4f);
					c.b -= id * 4f;
					c.b -= c.b >= 2f ? 2f : 0f;
					pixels[i] = c.a > 0f ? new Color(c.r, c.g, c.b, 1f).gamma : Color.black;
				}

				var display = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
				display.SetPixels(pixels);
				display.Apply();
				File.WriteAllBytes(path, display.EncodeToPNG());
				Object.DestroyImmediate(display);
			}
			else
			{
				File.WriteAllBytes(path, copy.EncodeToPNG());
			}

			Object.DestroyImmediate(copy);
		}

		private static GameObject CreatePixelObject(string name, Mesh mesh, Material material, Vector3 position, Quaternion rotation, Vector3 scale)
		{
			var gameObject = new GameObject(name) { layer = PixelRenderManager.Layer };
			gameObject.transform.SetPositionAndRotation(position, rotation);
			gameObject.transform.localScale = scale;
			gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
			var renderer = gameObject.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = material;
			renderer.shadowCastingMode = ShadowCastingMode.On;
			renderer.receiveShadows = true;
			gameObject.AddComponent<PixelRenderObject>();
			return gameObject;
		}

		private static Material CreateMaterial(string name, Color a, Color b, Vector2 checks)
		{
			string path = $"{MaterialFolder}/{name}.mat";
			var material = AssetDatabase.LoadAssetAtPath<Material>(path);

			// Уже существующий материал не трогаем — в нём могут быть твои настройки.
			if (material != null)
				return material;

			material = new Material(Shader.Find("EliotByte/PixelRender/Legacy/Source"));
			material.SetColor("_ColorA", a);
			material.SetColor("_ColorB", b);
			material.SetVector("_Checks", new Vector4(checks.x, checks.y, 0f, 0f));
			material.SetFloat("_TexelLighting", 0f);
			AssetDatabase.CreateAsset(material, path);
			return material;
		}

		private static Mesh SaveMesh(Mesh mesh)
		{
			string path = $"{MeshFolder}/{mesh.name.Replace(' ', '_')}.asset";
			var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);

			if (existing != null)
			{
				EditorUtility.CopySerialized(mesh, existing);
				Object.DestroyImmediate(mesh);
				return existing;
			}

			AssetDatabase.CreateAsset(mesh, path);
			return mesh;
		}

		private static Mesh PrimitiveMesh(PrimitiveType type)
		{
			var temp = GameObject.CreatePrimitive(type);
			var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
			Object.DestroyImmediate(temp);
			return mesh;
		}
	}
}
