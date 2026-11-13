using UnityEngine;

namespace EliotByte.PixelRender.Legacy.Demo
{
	/// <summary>
	/// Сцена PixelRender: с неба постепенно падают разноцветные капсулы — равномерно по всей площади пола,
	/// всего Total штук. Сверху слева — счётчик «Capsules: N».
	/// Равномерно = площадь делится на сетку клеток (примерно по клетке на капсулу), каждая капсула падает в свою
	/// клетку (в случайную точку внутри неё), а клетки перемешаны — так пол покрывается ровно, без пустот и кучек.
	/// </summary>
	public class PixelRainSpawner : MonoBehaviour
	{
		public Material[] Materials;
		[Min(1)] public int Total = 300;
		[Min(0.01f)] public float Interval = 0.12f;
		public float Height = 18f;
		[Tooltip("Половина стороны квадрата, по которому падают капсулы (пол 40×40 → 19).")]
		public float HalfExtent = 19f;
		[Tooltip("Радиус капсулы (min, max); полная высота — 3.5…4.5 радиуса (как на примере: длина ≈ радиус шара).")]
		public Vector2 RadiusRange = new(0.4f, 0.65f);
		public int Seed = 1;
		public bool ShowCounter = true;

		[Header("Невидимые стены по краю пола")]
		public bool Walls = true;
		[Tooltip("Половина стороны пола (пол 10×10 → 5).")]
		public float WallHalfExtent = 5f;
		public float WallHeight = 30f;

		private int _spawned;
		private float _timer;
		private System.Random _random;
		private Mesh _capsuleMesh;
		private GUIStyle _style;
		private int[] _cellOrder;
		private int _gridSize;

		private Mesh CapsuleMesh
		{
			get
			{
				if (_capsuleMesh == null)
				{
					var temp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
					_capsuleMesh = temp.GetComponent<MeshFilter>().sharedMesh;

					if (Application.isPlaying)
						Destroy(temp);
					else
						DestroyImmediate(temp);
				}

				return _capsuleMesh;
			}
		}

		public int Count => _spawned;

		private void Start()
		{
			if (!Walls)
				return;

			// Четыре невидимых коллайдера по краям пола — капсулы не скатываются за пол.
			const float thickness = 2f;
			float offset = WallHalfExtent + thickness * 0.5f;
			float length = (WallHalfExtent + thickness) * 2f;
			CreateWall(new Vector3(offset, WallHeight * 0.5f, 0f), new Vector3(thickness, WallHeight, length));
			CreateWall(new Vector3(-offset, WallHeight * 0.5f, 0f), new Vector3(thickness, WallHeight, length));
			CreateWall(new Vector3(0f, WallHeight * 0.5f, offset), new Vector3(length, WallHeight, thickness));
			CreateWall(new Vector3(0f, WallHeight * 0.5f, -offset), new Vector3(length, WallHeight, thickness));
		}

		private void CreateWall(Vector3 position, Vector3 size)
		{
			var wall = new GameObject("Invisible Wall");
			wall.transform.SetParent(transform, false);
			wall.transform.position = position;
			wall.AddComponent<BoxCollider>().size = size;
		}

		private void Update()
		{
			if (_spawned >= Total)
				return;

			_timer += Time.deltaTime;

			while (_timer >= Interval && _spawned < Total)
			{
				_timer -= Interval;
				Spawn();
			}
		}

		/// <summary>Одна капсула в случайной точке над полом (публично — для снимка в редакторе).</summary>
		public GameObject Spawn(float? height = null)
		{
			_random ??= new System.Random(Seed);

			if (Materials == null || Materials.Length == 0)
				return null;

			if (_cellOrder == null)
				BuildCellOrder();

			// Своя клетка сетки + случайная точка внутри неё.
			int cellIndex = _cellOrder[_spawned % _cellOrder.Length];
			float cellSize = HalfExtent * 2f / _gridSize;
			float x = -HalfExtent + (cellIndex % _gridSize + (float)_random.NextDouble()) * cellSize;
			float z = -HalfExtent + (cellIndex / _gridSize + (float)_random.NextDouble()) * cellSize;
			float radius = Mathf.Lerp(RadiusRange.x, RadiusRange.y, (float)_random.NextDouble());
			float length = radius * Mathf.Lerp(3.5f, 4.5f, (float)_random.NextDouble());

			var capsule = new GameObject("Capsule");
			capsule.transform.SetParent(transform, false);
			capsule.transform.position = new Vector3(x, height ?? Height + (float)_random.NextDouble() * 4f, z);
			capsule.transform.rotation = Quaternion.Euler((float)_random.NextDouble() * 360f, (float)_random.NextDouble() * 360f, (float)_random.NextDouble() * 360f);
			// Примитив-капсула: радиус 0.5, высота 2 → масштаб.
			capsule.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
			capsule.AddComponent<MeshFilter>().sharedMesh = CapsuleMesh;
			capsule.AddComponent<MeshRenderer>().sharedMaterial = Materials[_random.Next(Materials.Length)];
			capsule.AddComponent<CapsuleCollider>();

			if (Application.isPlaying)
			{
				var body = capsule.AddComponent<Rigidbody>();
				body.mass = radius * radius * length * 20f;
				body.interpolation = RigidbodyInterpolation.Interpolate;
				// Падают с ~20 м: без непрерывной проверки столкновений тонкий пол «проскакивают» за один шаг физики.
				body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
			}

			capsule.AddComponent<PixelRenderObject>();
			_spawned++;
			return capsule;
		}

		/// <summary>Клетки сетки gridSize×gridSize (не меньше Total), в случайном порядке.</summary>
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
			GUI.Label(new Rect(20f, 14f, 600f, 60f), $"Capsules: {_spawned}", _style);
		}
	}
}
