using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace EliotByte.PixelRender.Demo.Editor
{
	/// <summary>
	/// Создание демо-сцен пакета:
	///  • PixelRender.unity — шар, колонны и пол, с неба падает 1000 капсул (инстансинг);
	///  • PixelRenderTerrain.unity — low-poly террейн с текстурами, клетчатый куб, дождь капсул по всему террейну,
	///    камера медленно облетает террейн.
	/// Меню Tools → Pixel Render или файл PixelRenderDemo.request в корне проекта (scene / terrain / all).
	/// Уже существующие материалы и меши не перезаписываются.
	/// </summary>
	[InitializeOnLoad]
	public static class PixelRenderDemoCreator
	{
		public const string Root = "Assets/External/PixelRender/Demo";
		public const string ScenePath = Root + "/Scenes/PixelRender.unity";
		public const string TerrainScenePath = Root + "/Scenes/PixelRenderTerrain.unity";
		private const string LegacyRoot = "Assets/External/PixelRender/Legacy/Demo";
		private const string MaterialFolder = Root + "/Materials";
		private const string MeshFolder = Root + "/Meshes";
		private const string TextureFolder = Root + "/Textures";
		private const string SourceShader = "EliotByte/PixelRender/Source";
		private const string Request = "PixelRenderDemo.request";
		private const string Output = "SandboxCaptures/PixelRender/demo_create.txt";
		private const float ColumnHeight = 6f;
		private static readonly string[] Palette = { "Orange", "RedYellow", "Blue", "Green", "Pink" };
		private static double _nextPoll;

		/// <summary>Откуда берётся террейн для демо (сцена песочницы проекта; в самом пакете уже лежат готовые ассеты).</summary>
		private const string TerrainSourceScene = "Assets/Game/Sandbox/Scenes/TerrainSandbox_PolygonPixel.unity";

		static PixelRenderDemoCreator()
		{
			EditorApplication.update -= Poll;
			EditorApplication.update += Poll;
		}

		private static void Poll()
		{
			if (EditorApplication.timeSinceStartup < _nextPoll)
				return;

			_nextPoll = EditorApplication.timeSinceStartup + 1.0;

			if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
				return;

			string label = File.ReadAllText(Request).Trim();
			File.Delete(Request);
			Directory.CreateDirectory(Path.GetDirectoryName(Output));

			if (SceneManager.GetActiveScene().isDirty)
			{
				File.WriteAllText(Output, "SKIPPED: открытая сцена не сохранена.");
				return;
			}

			try
			{
				if (label == "scene" || label == "all") CreateScene();
				if (label == "terrain" || label == "all") CreateTerrainScene();
				File.WriteAllText(Output, "OK " + label);
			}
			catch (System.Exception exception)
			{
				File.WriteAllText(Output, "EXCEPTION\n" + exception);
			}
		}

		[MenuItem("Tools/Pixel Render/Create Demo Scene")]
		public static void CreateScene()
		{
			EnsureFolders();
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			var ball = CheckerMaterial("Ball", new Color(0.96f, 0.96f, 0.94f), new Color(0.04f, 0.04f, 0.05f), new Vector2(3f, 3f));
			var column = CheckerMaterial("Column", new Color(0.93f, 0.89f, 0.80f), new Color(0.10f, 0.10f, 0.11f), new Vector2(4f, 4f));
			var floor = CheckerMaterial("Floor", new Color(0.30f, 0.30f, 0.32f), new Color(0.24f, 0.24f, 0.26f), new Vector2(1f, 1f));
			var palette = PaletteMaterials();

			var light = new GameObject("Directional Light").AddComponent<Light>();
			light.type = LightType.Directional;
			light.intensity = 1.1f;
			light.color = new Color(1f, 0.97f, 0.92f);
			light.shadows = LightShadows.None;
			light.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = new Color(0.42f, 0.44f, 0.50f);
			RenderSettings.ambientEquatorColor = new Color(0.30f, 0.30f, 0.33f);
			RenderSettings.ambientGroundColor = new Color(0.16f, 0.16f, 0.18f);
			RenderSettings.fog = false;
			RenderSettings.skybox = null;

			var camera = CreateCamera(Color.black, 50f, 200f);
			var motion = camera.gameObject.AddComponent<PixelRenderCameraMotion>();
			motion.Apply(0f);

			var floorObject = PixelObject("Floor", CopyMesh($"{LegacyRoot}/Meshes/PixelRender_Floor.asset", "PixelRender_Floor"), floor,
				new Vector3(0f, -0.2f, 0f), Quaternion.identity, Vector3.one);
			var floorCollider = floorObject.AddComponent<BoxCollider>();
			floorCollider.size = new Vector3(20f, 4f, 20f);
			floorCollider.center = new Vector3(0f, -1.8f, 0f);

			var ballObject = PixelObject("Checker Ball", PrimitiveMesh(PrimitiveType.Sphere), ball, new Vector3(0f, 2.2f, 0f), Quaternion.Euler(0f, 20f, 0f), Vector3.one * 4.4f);
			ballObject.AddComponent<SphereCollider>();

			var columnMesh = CopyMesh($"{LegacyRoot}/Meshes/PixelRender_Column.asset", "PixelRender_Column");
			var columnPlaces = new[]
			{
				(new Vector3(-4.5f, ColumnHeight * 0.5f, 2.25f), Quaternion.Euler(0f, 15f, 0f)),
				(new Vector3(4.125f, ColumnHeight * 0.5f, 3.75f), Quaternion.Euler(0f, 40f, 0f)),
				(new Vector3(5.25f, ColumnHeight * 0.5f, -3f), Quaternion.Euler(0f, -10f, 0f)),
				(new Vector3(-3.75f, ColumnHeight * 0.5f, -4.5f), Quaternion.Euler(0f, 30f, 0f))
			};

			for (int i = 0; i < columnPlaces.Length; i++)
				PixelObject($"Column {i + 1}", columnMesh, column, columnPlaces[i].Item1, columnPlaces[i].Item2, Vector3.one).AddComponent<BoxCollider>();

			var rain = new GameObject("Capsule Rain").AddComponent<PixelRainSpawner>();
			rain.Materials = palette;
			rain.Ground = floorObject.GetComponent<MeshRenderer>();
			rain.Margin = 0.4f;
			rain.Height = 18f;

			EditorSceneManager.SaveScene(scene, ScenePath);
			AssetDatabase.SaveAssets();
		}

		[MenuItem("Tools/Pixel Render/Create Terrain Demo Scene")]
		public static void CreateTerrainScene()
		{
			EnsureFolders();

			// Ассеты террейна (меш, материал, текстуры) — из песочницы проекта, если их ещё нет в пакете.
			var terrainMesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{MeshFolder}/PixelRender_Terrain.asset");
			var terrainMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/Terrain.mat");
			Color background = new(0.62f, 0.68f, 0.66f);
			var sunRotation = Quaternion.Euler(38f, -40f, 0f);
			Color sunColor = new(1f, 0.95f, 0.86f);
			float sunIntensity = 1.1f;
			string managerJson = null;
			bool fog = true;
			FogMode fogMode = FogMode.ExponentialSquared;
			float fogDensity = 0.0075f;
			Color fogColor = background;
			Vector3 cubePosition = new(39.65f, 0f, 72.8f);

			if (File.Exists(TerrainSourceScene))
			{
				var source = EditorSceneManager.OpenScene(TerrainSourceScene, OpenSceneMode.Single);

				foreach (var root in source.GetRootGameObjects())
				{
					if (root.name.StartsWith("Terrain") && root.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
					{
						if (terrainMesh == null)
						{
							terrainMesh = Object.Instantiate(filter.sharedMesh);
							terrainMesh.name = "PixelRender_Terrain";
							AssetDatabase.CreateAsset(terrainMesh, $"{MeshFolder}/PixelRender_Terrain.asset");
						}

						if (terrainMaterial == null)
							terrainMaterial = CopyTerrainMaterial(root.GetComponent<MeshRenderer>().sharedMaterial);
					}

					if (root.TryGetComponent(out Light light) && light.type == LightType.Directional)
					{
						sunRotation = light.transform.rotation;
						sunColor = light.color;
						sunIntensity = light.intensity;
					}

					if (root.TryGetComponent(out Camera sourceCamera))
					{
						background = sourceCamera.backgroundColor;

						if (root.TryGetComponent(out PixelRenderManager manager))
							managerJson = EditorJsonUtility.ToJson(manager);
					}

					if (root.name == "Cube (Polygon Pixel)")
						cubePosition = root.transform.position;
				}

				fog = RenderSettings.fog;
				fogMode = RenderSettings.fogMode;
				fogDensity = RenderSettings.fogDensity;
				fogColor = RenderSettings.fogColor;
			}

			if (terrainMesh == null || terrainMaterial == null)
				throw new System.Exception("Нет ассетов террейна (Demo/Meshes/PixelRender_Terrain.asset, Demo/Materials/Terrain.mat) и сцены-источника " + TerrainSourceScene);

			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			RenderSettings.skybox = null;
			RenderSettings.fog = fog;
			RenderSettings.fogMode = fogMode;
			RenderSettings.fogDensity = fogDensity;
			RenderSettings.fogColor = fogColor;
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = new Color(0.55f, 0.6f, 0.62f);
			RenderSettings.ambientEquatorColor = new Color(0.42f, 0.44f, 0.42f);
			RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.22f);

			var sun = new GameObject("Sun").AddComponent<Light>();
			sun.type = LightType.Directional;
			sun.color = sunColor;
			sun.intensity = sunIntensity;
			sun.shadows = LightShadows.None;
			sun.transform.rotation = sunRotation;

			var terrain = PixelObject("Terrain", terrainMesh, terrainMaterial, Vector3.zero, Quaternion.identity, Vector3.one);
			terrain.AddComponent<MeshCollider>().sharedMesh = terrainMesh;
			Bounds bounds = terrain.GetComponent<MeshRenderer>().bounds;

			var cubeMesh = CopyMesh($"{LegacyRoot}/Meshes/PixelRender_Test_Cube.asset", "PixelRender_Cube");
			var cube = PixelObject("Checker Cube", cubeMesh, CheckerMaterial("Ball", Color.white, Color.black, new Vector2(3f, 3f)),
				cubePosition, Quaternion.Euler(0f, 30f, 0f), Vector3.one);
			cube.AddComponent<BoxCollider>();

			var camera = CreateCamera(background, 60f, 600f);

			if (managerJson != null)
				EditorJsonUtility.FromJsonOverwrite(managerJson, camera.GetComponent<PixelRenderManager>());

			var motion = camera.gameObject.AddComponent<PixelRenderCameraMotion>();
			motion.Center = new Vector3(bounds.center.x, bounds.min.y + 2f, bounds.center.z);
			motion.Distance = bounds.size.x * 0.45f;
			motion.Pitch = 28f;
			motion.OrbitSpeed = 2f;
			motion.DistanceAmplitude = 6f;
			motion.DistancePeriod = 40f;
			motion.HeightAmplitude = 2f;
			motion.HeightPeriod = 25f;
			motion.Apply(0f);

			var rain = new GameObject("Capsule Rain").AddComponent<PixelRainSpawner>();
			rain.Materials = PaletteMaterials();
			rain.Ground = terrain.GetComponent<MeshRenderer>();
			rain.Height = 18f;

			EditorSceneManager.SaveScene(scene, TerrainScenePath);
			AssetDatabase.SaveAssets();
		}

		private static void EnsureFolders()
		{
			foreach (string folder in new[] { MaterialFolder, MeshFolder, TextureFolder, Root + "/Scenes" })
				Directory.CreateDirectory(folder);

			AssetDatabase.Refresh();
		}

		private static Camera CreateCamera(Color background, float fov, float far)
		{
			var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
			var camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = background;
			camera.fieldOfView = fov;
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = far;
			cameraObject.AddComponent<AudioListener>();
			cameraObject.AddComponent<PixelRenderManager>();
			return camera;
		}

		private static GameObject PixelObject(string name, Mesh mesh, Material material, Vector3 position, Quaternion rotation, Vector3 scale)
		{
			var gameObject = new GameObject(name) { layer = PixelRenderManager.Layer };
			gameObject.transform.SetPositionAndRotation(position, rotation);
			gameObject.transform.localScale = scale;
			gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
			var renderer = gameObject.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = material;
			renderer.shadowCastingMode = ShadowCastingMode.On;
			renderer.receiveShadows = true;
			return gameObject;
		}

		private static Material[] PaletteMaterials()
		{
			var colors = new (Color, Color)[]
			{
				(new Color(1.00f, 0.58f, 0.08f), new Color(0.12f, 0.08f, 0.05f)),
				(new Color(0.92f, 0.16f, 0.10f), new Color(1.00f, 0.84f, 0.18f)),
				(new Color(0.14f, 0.38f, 0.95f), new Color(0.45f, 0.85f, 1.00f)),
				(new Color(0.30f, 0.82f, 0.22f), new Color(0.08f, 0.42f, 0.10f)),
				(new Color(0.96f, 0.42f, 0.86f), new Color(0.50f, 0.20f, 0.76f))
			};

			var materials = new Material[Palette.Length];

			for (int i = 0; i < Palette.Length; i++)
				materials[i] = CheckerMaterial(Palette[i], colors[i].Item1, colors[i].Item2, new Vector2(8f, 6f));

			return materials;
		}

		/// <summary>Клетчатый материал на шейдере пакета (существующий не трогаем — в нём могут быть ручные настройки).</summary>
		private static Material CheckerMaterial(string name, Color a, Color b, Vector2 checks)
		{
			string path = $"{MaterialFolder}/{name}.mat";
			var material = AssetDatabase.LoadAssetAtPath<Material>(path);

			if (material != null)
				return material;

			material = new Material(Shader.Find(SourceShader));
			material.SetFloat("_Layers", 0f);
			material.SetColor("_ColorA", a);
			material.SetColor("_ColorB", b);
			material.SetVector("_Checks", new Vector4(checks.x, checks.y, 0f, 0f));
			material.SetFloat("_Lit", 0f);
			AssetDatabase.CreateAsset(material, path);
			return material;
		}

		/// <summary>Копия материала террейна с копиями его текстур в Demo/Textures.</summary>
		private static Material CopyTerrainMaterial(Material source)
		{
			var material = new Material(source) { name = "Terrain" };

			foreach (string property in source.GetTexturePropertyNames())
			{
				var texture = source.GetTexture(property);

				if (texture == null)
					continue;

				string from = AssetDatabase.GetAssetPath(texture);

				if (string.IsNullOrEmpty(from) || from.StartsWith(Root))
					continue;

				string to = $"{TextureFolder}/{Path.GetFileName(from)}";

				if (!File.Exists(to))
					AssetDatabase.CopyAsset(from, to);

				material.SetTexture(property, AssetDatabase.LoadAssetAtPath<Texture>(to));
			}

			AssetDatabase.CreateAsset(material, $"{MaterialFolder}/Terrain.mat");
			return material;
		}

		private static Mesh CopyMesh(string from, string name)
		{
			string path = $"{MeshFolder}/{name}.asset";
			var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);

			if (existing != null)
				return existing;

			var source = AssetDatabase.LoadAssetAtPath<Mesh>(from);

			if (source == null)
				throw new System.Exception("Нет меша " + from);

			var copy = Object.Instantiate(source);
			copy.name = name;
			AssetDatabase.CreateAsset(copy, path);
			return copy;
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
