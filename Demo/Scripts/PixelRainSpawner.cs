using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EliotByte.PixelRender.Demo
{
	/// <summary>
	/// Демо: с неба постепенно падают разноцветные капсулы — равномерно по области Ground (или квадрату HalfExtent
	/// вокруг объекта), всего Total штук; по краям области — невидимые стены.
	/// Капсулы рисуются пиксельным рендером (слой PixelRenderManager.Layer). Instanced: у капсул нет MeshRenderer,
	/// они рисуются инстансингом — один вызов отрисовки на материал (Graphics.RenderMeshInstanced).
	/// Неподвижным объектам слоя без коллайдера в Play добавляется MeshCollider, чтобы капсулам было на что падать.
	/// Сверху слева — счётчик капсул и время кадра.
	/// </summary>
	public class PixelRainSpawner : MonoBehaviour
	{
		public Material[] Materials;
		[Min(1)] public int Total = 1000;
		[Min(0.001f)] public float Interval = 0.12f;
		[Tooltip("Высота появления над землёй, м.")]
		public float Height = 18f;
		[Tooltip("Область дождя — границы этого объекта по XZ (например, террейн). Пусто — квадрат HalfExtent вокруг объекта дождя.")]
		public Renderer Ground;
		[Tooltip("Половина стороны квадрата дождя, если Ground не задан.")]
		public float HalfExtent = 9.6f;
		[Tooltip("Отступ области дождя от края, м.")]
		public float Margin = 1f;
		[Tooltip("Невидимые стены по краю области.")]
		public bool Walls = true;
		[Tooltip("Высота невидимых стен над самой высокой точкой земли, м.")]
		public float WallHeight = 40f;
		[Tooltip("Радиус капсулы (min, max); полная высота — 3.5…4.5 радиуса.")]
		public Vector2 RadiusRange = new(0.4f, 0.65f);
		public int Seed = 1;
		public bool ShowCounter = true;
		[Tooltip("Рисовать капсулы инстансингом: один вызов отрисовки на материал вместо одного на капсулу.")]
		public bool Instanced = true;

		private int _spawned;
		private float _timer;
		private System.Random _random;
		private Mesh _capsuleMesh;
		private GUIStyle _style;
		private int[] _cellOrder;
		private int _gridSize;
		private float _frameMs;
		private Rect _area;
		private float _top;
		private List<Transform>[] _instances;
		private Material[] _instancedMaterials;
		private readonly List<Matrix4x4> _matrices = new();

		public int Count => _spawned;
		/// <summary>Новые капсулы не появляются (капсулы при этом рисуются — для замеров).</summary>
		[System.NonSerialized] public bool Paused;
		public Rect Area => _area;

		private Mesh CapsuleMesh
		{
			get
			{
				if (_capsuleMesh == null)
				{
					var temp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
					_capsuleMesh = temp.GetComponent<MeshFilter>().sharedMesh;
					Destroy(temp);
				}

				return _capsuleMesh;
			}
		}

		private void Start()
		{
			foreach (var filter in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
			{
				if (filter.gameObject.layer != PixelRenderManager.Layer || filter.sharedMesh == null)
					continue;

				if (filter.GetComponentInParent<Rigidbody>() != null || filter.GetComponent<Collider>() != null)
					continue;

				filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
			}

			Bounds bounds = Ground != null
				? Ground.bounds
				: new Bounds(transform.position, new Vector3(HalfExtent * 2f, 0f, HalfExtent * 2f));
			_area = Rect.MinMaxRect(bounds.min.x + Margin, bounds.min.z + Margin, bounds.max.x - Margin, bounds.max.z - Margin);
			_top = bounds.max.y;

			if (!Walls)
				return;

			// Невидимые стены по краю террейна.
			const float thickness = 2f;
			float bottom = bounds.min.y - 5f;
			float top = bounds.max.y + WallHeight;
			CreateWall(new Vector3(bounds.min.x - thickness * 0.5f, 0f, bounds.center.z), new Vector3(thickness, 0f, bounds.size.z + thickness * 2f), bottom, top);
			CreateWall(new Vector3(bounds.max.x + thickness * 0.5f, 0f, bounds.center.z), new Vector3(thickness, 0f, bounds.size.z + thickness * 2f), bottom, top);
			CreateWall(new Vector3(bounds.center.x, 0f, bounds.min.z - thickness * 0.5f), new Vector3(bounds.size.x + thickness * 2f, 0f, thickness), bottom, top);
			CreateWall(new Vector3(bounds.center.x, 0f, bounds.max.z + thickness * 0.5f), new Vector3(bounds.size.x + thickness * 2f, 0f, thickness), bottom, top);
		}

		private void CreateWall(Vector3 center, Vector3 size, float bottom, float top)
		{
			var wall = new GameObject("Invisible Wall");
			wall.transform.SetParent(transform, false);
			wall.transform.position = new Vector3(center.x, (bottom + top) * 0.5f, center.z);
			wall.AddComponent<BoxCollider>().size = new Vector3(size.x, top - bottom, size.z);
		}

		private void Update()
		{
			_frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.05f);

			if (_spawned >= Total || Paused)
				return;

			_timer += Time.deltaTime;

			while (_timer >= Interval && _spawned < Total)
			{
				_timer -= Interval;
				Spawn();
			}
		}

		public GameObject Spawn()
		{
			_random ??= new System.Random(Seed);

			if (Materials == null || Materials.Length == 0)
				return null;

			if (_cellOrder == null)
				BuildCellOrder();

			int cellIndex = _cellOrder[_spawned % _cellOrder.Length];
			float x = _area.xMin + (cellIndex % _gridSize + (float)_random.NextDouble()) * _area.width / _gridSize;
			float z = _area.yMin + (cellIndex / _gridSize + (float)_random.NextDouble()) * _area.height / _gridSize;
			// Высота земли под точкой — лучом сверху вниз по коллайдерам.
			float ground = _top;

			if (Physics.Raycast(new Vector3(x, _top + 1000f, z), Vector3.down, out RaycastHit hit, 3000f, ~0, QueryTriggerInteraction.Ignore))
				ground = hit.point.y;

			float radius = Mathf.Lerp(RadiusRange.x, RadiusRange.y, (float)_random.NextDouble());
			float length = radius * Mathf.Lerp(3.5f, 4.5f, (float)_random.NextDouble());

			var capsule = new GameObject("Capsule") { layer = PixelRenderManager.Layer };
			capsule.transform.SetParent(transform, false);
			capsule.transform.position = new Vector3(x, ground + Height + (float)_random.NextDouble() * 4f, z);
			capsule.transform.rotation = Quaternion.Euler((float)_random.NextDouble() * 360f, (float)_random.NextDouble() * 360f, (float)_random.NextDouble() * 360f);
			capsule.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
			int materialIndex = _random.Next(Materials.Length);

			if (Instanced)
			{
				_instances ??= new List<Transform>[Materials.Length];
				(_instances[materialIndex] ??= new List<Transform>()).Add(capsule.transform);
			}
			else
			{
				capsule.AddComponent<MeshFilter>().sharedMesh = CapsuleMesh;
				capsule.AddComponent<MeshRenderer>().sharedMaterial = Materials[materialIndex];
			}
			capsule.AddComponent<CapsuleCollider>();

			var body = capsule.AddComponent<Rigidbody>();
			body.mass = radius * radius * length * 20f;
			body.interpolation = RigidbodyInterpolation.Interpolate;
			body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

			_spawned++;
			return capsule;
		}

		private void LateUpdate()
		{
			if (!Instanced || _instances == null)
				return;

			if (_instancedMaterials == null)
			{
				// Копии материалов с включённым инстансингом (ассеты не трогаем).
				_instancedMaterials = new Material[Materials.Length];

				for (int i = 0; i < Materials.Length; i++)
					if (Materials[i] != null)
						_instancedMaterials[i] = new Material(Materials[i]) { enableInstancing = true, name = Materials[i].name + " (Instanced)" };
			}

			var bounds = new Bounds(new Vector3(_area.center.x, 0f, _area.center.y), new Vector3(_area.width + 10f, 1000f, _area.height + 10f));

			for (int i = 0; i < _instances.Length; i++)
			{
				var list = _instances[i];

				if (list == null || list.Count == 0 || _instancedMaterials[i] == null)
					continue;

				_matrices.Clear();

				foreach (var t in list)
					_matrices.Add(t.localToWorldMatrix);

				var renderParams = new RenderParams(_instancedMaterials[i])
				{
					layer = PixelRenderManager.Layer,
					renderingLayerMask = uint.MaxValue,
					worldBounds = bounds,
					shadowCastingMode = ShadowCastingMode.On,
					receiveShadows = true
				};

				Graphics.RenderMeshInstanced(renderParams, CapsuleMesh, 0, _matrices);
			}
		}

		private void OnDestroy()
		{
			if (_instancedMaterials == null)
				return;

			foreach (var material in _instancedMaterials)
				if (material != null)
					Destroy(material);
		}

		private void BuildCellOrder()
		{
			_gridSize = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(Total)));
			_cellOrder = new int[_gridSize * _gridSize];

			for (int i = 0; i < _cellOrder.Length; i++)
				_cellOrder[i] = i;

			for (int i = _cellOrder.Length - 1; i > 0; i--)
			{
				int j = _random.Next(i + 1);
				(_cellOrder[i], _cellOrder[j]) = (_cellOrder[j], _cellOrder[i]);
			}
		}

		private void OnGUI()
		{
			if (!ShowCounter)
				return;

			_style ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(18, Screen.height / 28), fontStyle = FontStyle.Bold };
			_style.normal.textColor = Color.white;
			GUI.Label(new Rect(20f, 14f, 800f, 60f), $"Capsules: {_spawned}   {_frameMs:0.0} ms", _style);
		}
	}
}
