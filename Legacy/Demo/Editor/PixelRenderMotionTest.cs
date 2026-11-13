using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
	/// Тест мерцания сцены PixelRender: камера летит как в Play (PixelRenderCameraMotion), кадры по 1/60 с.
	/// Считает «мигания» — пиксели, где кадр i отличается от i-1 и i+1, а i-1 и i+1 совпадают (A→B→A).
	/// Плавное движение даёт смены, но почти не даёт миганий; мерцание — даёт.
	/// Меню: Tools/Pixel Render/Pixel Render Motion Test или файл PixelRenderMotion.request в корне проекта
	/// → SandboxCaptures/PixelRender/motion.txt, motion_flicker.png (карта миганий), motion_frame*.png.
	/// </summary>
	[InitializeOnLoad]
	public static class PixelRenderMotionTest
	{
		private const string Request = "PixelRenderMotion.request";
		private const string OutputFolder = "SandboxCaptures/PixelRender";
		private const int Width = 1280;
		private const int Height = 720;
		private const int Frames = 90;
		private const float Step = 1f / 60f;

		private static double _nextPoll;
		private static Color[] _lastSource;
		private static long _cellsChecked, _cellsMixed;
		private static int[] _mixedHeat;

		static PixelRenderMotionTest()
		{
			EditorApplication.update -= Poll;
			EditorApplication.update += Poll;
		}

		private static void Poll()
		{
			if (EditorApplication.timeSinceStartup < _nextPoll)
				return;

			_nextPoll = EditorApplication.timeSinceStartup + 1.0;

			if (!File.Exists(Request))
				return;

			if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
				return;

			AssetDatabase.Refresh();

			if (EditorApplication.isCompiling || EditorApplication.isUpdating)
				return;

			string label = File.ReadAllText(Request).Trim();
			File.Delete(Request);
			Directory.CreateDirectory(OutputFolder);

			// Открытую сцену PixelRender тестируем как есть (с несохранёнными правками); переключаться с другой
			// несохранённой сцены не будем.
			if (SceneManager.GetActiveScene().isDirty && SceneManager.GetActiveScene().path != PixelRenderSceneCreator.ScenePath
			    && SceneManager.GetActiveScene().path != PixelRenderSceneCreator.FloorTestScenePath)
			{
				File.WriteAllText(Path.Combine(OutputFolder, "motion.txt"), "SKIPPED: открытая сцена не сохранена.");
				return;
			}

			string previous = SceneManager.GetActiveScene().path;
			Run(string.IsNullOrEmpty(label) ? "motion" : label);

			if (!string.IsNullOrEmpty(previous) && previous != SceneFor(label))
				EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
		}

		/// <summary>Метка с «floortest» — тест на сцене пола (белый пол + оранжевый куб), иначе — демо-сцена.</summary>
		private static string SceneFor(string label) =>
			label.Contains("floortest") ? PixelRenderSceneCreator.FloorTestScenePath : PixelRenderSceneCreator.ScenePath;

		[MenuItem("Tools/Pixel Render/Legacy/Pixel Render Motion Test")]
		public static void RunMenu() => Run("motion");

		public static void Run(string label)
		{
			string scenePath = SceneFor(label);

			if (scenePath == PixelRenderSceneCreator.FloorTestScenePath && !File.Exists(scenePath))
				PixelRenderSceneCreator.CreateFloorTestScene();

			if (SceneManager.GetActiveScene().path != scenePath)
				EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

			var report = new StringBuilder();

			foreach (string shaderName in new[] { "EliotByte/PixelRender/Legacy/Source", "EliotByte/PixelRender/Legacy/Resolve" })
			{
				var shader = Shader.Find(shaderName);

				if (shader == null)
					report.AppendLine($"{shaderName}: НЕ НАЙДЕН");
				else
					foreach (var message in ShaderUtil.GetShaderMessages(shader))
						report.AppendLine($"{shaderName}: {message.severity} line {message.line}: {message.message}");
			}
			var manager = Object.FindFirstObjectByType<PixelRenderManager>();
			var camera = manager != null ? manager.GetComponent<Camera>() : null;
			var motion = camera != null ? camera.GetComponent<PixelRenderCameraMotion>() : null;

			if (camera == null || motion == null)
			{
				File.WriteAllText(Path.Combine(OutputFolder, label + ".txt"), "Нет камеры с PixelRenderManager / PixelRenderCameraMotion.");
				return;
			}

			_cellsChecked = _cellsMixed = 0;
			PixelRenderObject.BakesByError = PixelRenderObject.BakesByThresholds = 0;
			PixelRenderManager.SurfaceBakes = 0;
			_mixedHeat = new int[Width * Height];
			var capsules = SpawnPreviewCapsules();
			var objects = Object.FindObjectsByType<PixelRenderObject>(FindObjectsSortMode.None);
			var screen = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
			var readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
			var sourceReadback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
			var savedTarget = camera.targetTexture;
			camera.targetTexture = screen;

			// Варианты для поиска причины: метка с «noshadow» — без теней, «unlit» — без света вообще.
			var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
			var savedShadows = new LightShadows[lights.Length];

			for (int i = 0; i < lights.Length; i++)
			{
				savedShadows[i] = lights[i].shadows;

				if (label.Contains("noshadow"))
					lights[i].shadows = LightShadows.None;
			}

			Shader.SetGlobalFloat("_PixelDebugUnlit", label.Contains("unlit") ? 1f : 0f);
			Shader.SetGlobalFloat("_PixelDebugPaths", label.Contains("paths") ? 1f : 0f);
			PixelRenderObject.DisableMaskUpdates = label.Contains("noupd");
			bool savedLive = manager.Settings.LiveSilhouette;

			if (label.Contains("nolive"))
				manager.Settings.LiveSilhouette = false;
			bool savedBake = manager.Settings.SurfaceBake;
			float savedAngle = manager.Settings.BakeAngle, savedScale = manager.Settings.BakeScale, savedDissolve = manager.Settings.BakeDissolve;
			float savedError = manager.Settings.BakeMaxError;
			float savedEdge = manager.Settings.EdgeHysteresis;

			if (label.Contains("nobake"))
				manager.Settings.SurfaceBake = false;

			bool savedSilhouette = manager.Settings.BakeSilhouette;

			if (label.Contains("nosil"))
				manager.Settings.BakeSilhouette = false;

			var savedTransition = manager.Settings.Transition;

			if (label.Contains("dissolvemode"))
				manager.Settings.Transition = BakeTransition.Dissolve;
			else if (label.Contains("morphmode"))
				manager.Settings.Transition = BakeTransition.Morph;

			// Переопределения из метки: angle=45, scale=1, dissolve=0.
			foreach (string part in label.Split('_'))
			{
				string[] pair = part.Split('=');

				if (pair.Length != 2 || !float.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
					continue;

				if (pair[0] == "angle") manager.Settings.BakeAngle = value;
				if (pair[0] == "scale") manager.Settings.BakeScale = value;
				if (pair[0] == "dissolve") manager.Settings.BakeDissolve = value;
				if (pair[0] == "err") manager.Settings.BakeMaxError = value;
				if (pair[0] == "hys") manager.Settings.EdgeHysteresis = value;
			}

			var bakeCountsBefore = new Dictionary<PixelRenderObject, int>();

			try
			{
				manager.Initialize();

				foreach (var pixelObject in objects)
				{
					pixelObject.Initialize();
					bakeCountsBefore[pixelObject] = pixelObject.BakeCount;
				}

				// Метка «t=2.1» — один прогон с этого момента полёта камеры (как в Play).
				float[] startTimes = { 0f, 2.6f };

				foreach (string part in label.Split('_'))
					if (part.StartsWith("t=") && float.TryParse(part.Substring(2), System.Globalization.NumberStyles.Float,
						    System.Globalization.CultureInfo.InvariantCulture, out float customStart))
						startTimes = new[] { customStart };

				foreach (float startTime in startTimes)
				{
					var frames = new List<Color32[]>();
					var cells = new List<Dictionary<long, (Color32 color, int pixel)>>();
					var points = new List<Dictionary<long, Color32>>();
					var gtFrames = new List<Color32[]>();
					var excluded = new List<RectInt>(); // slide: прямоугольник самой движущейся капсулы — её не считаем

					for (int frame = 0; frame < Frames; frame++)
					{
						if (label.Contains("pullback") || label.Contains("approach"))
						{
							// pullback: камера вплотную к шару, потом быстро отлетает (2.5 → 14 м за 1.5 с), смотрит на шар.
							// approach — наоборот.
							float k = frame / (float)(Frames - 1);
							k = label.Contains("approach") ? 1f - k : k;
							float distance = Mathf.Lerp(2.5f, 14f, k * k);
							Vector3 target = motion.Center;

							// «column» — к верху колонны (как на скриншоте с белыми точками по верхнему краю).
							if (label.Contains("column"))
							{
								var column = GameObject.Find("Column 1");

								if (column != null)
								{
									var columnBounds = column.GetComponent<Renderer>().bounds;
									target = new Vector3(columnBounds.center.x, columnBounds.max.y, columnBounds.center.z);
								}
							}

							// «top» — сверху, чтобы за верхним краем колонны был пол (как на скриншоте).
							float pitch = label.Contains("top") ? 50f : label.Contains("column") ? 18f : 30f;
							Vector3 direction = Quaternion.Euler(pitch, 25f + startTime * 40f, 0f) * Vector3.back;
							camera.transform.position = target + direction * distance;
							camera.transform.LookAt(target);
						}
						else if (label.Contains("slide"))
						{
							// Капсула проезжает перед шаром слева направо (6 м за 1.5 с), камера стоит.
							motion.Apply(startTime);
							float k = frame / (float)(Frames - 1);
							Vector3 toCamera = (camera.transform.position - motion.Center).normalized;

							if (capsules.Count > 0)
							{
								capsules[0].transform.position = motion.Center + toCamera * 2.5f + camera.transform.right * Mathf.Lerp(-3f, 3f, k);
								capsules[0].transform.rotation = Quaternion.Euler(0f, 0f, 70f);
							}
						}
						else
							motion.Apply(startTime + frame * Step);

						frames.Add(Render(manager, camera, screen, readback));

						if (label.Contains("slide") && capsules.Count > 0)
						{
							var moverBounds = capsules[0].GetComponent<Renderer>().bounds;
							Vector3 c = camera.WorldToScreenPoint(moverBounds.center);
							float focalLength = 0.5f * Height / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
							int r = Mathf.CeilToInt(moverBounds.extents.magnitude * focalLength / Mathf.Max(c.z, 0.1f)) + 8;
							excluded.Add(new RectInt((int)c.x - r, (int)c.y - r, r * 2, r * 2));
						}


						var ids = ReadIds(manager.SourceTexture, sourceReadback);
						cells.Add(SampleCells(camera, objects, frames[frame], ids));
						points.Add(SamplePoints(camera, objects, frames[frame], ids));

						// Эталон (метка с «gt»), независимый от сборки: каждый объект рендерится один в кадре, пикселизуется
						// по своей сетке правилом «центр клетки», объекты складываются по глубине клеток.
						if (label.Contains("gt"))
							gtFrames.Add(RenderTruth(manager, camera, objects, sourceReadback));
					}

					// Мерцание в сетке объекта: клетка (объект, i, j) A→B→A. Движение объекта по экрану сюда не попадает.
					long cellFlicker = 0, cellChanges = 0, cellTotal = 0;
					var cellHeat = new int[Width * Height];

					for (int frame = 1; frame < Frames - 1; frame++)
					{
						foreach (var pair in cells[frame])
						{
							if (!cells[frame - 1].TryGetValue(pair.Key, out var before) || !cells[frame + 1].TryGetValue(pair.Key, out var after))
								continue;

							cellTotal++;

							if (!Same(before.color, pair.Value.color))
								cellChanges++;

							if (Same(before.color, after.color) && !Same(before.color, pair.Value.color))
							{
								cellFlicker++;
								int px = pair.Value.pixel % Width, py = pair.Value.pixel / Width;

								for (int dy = -2; dy <= 2; dy++)
								for (int dx = -2; dx <= 2; dx++)
									if (px + dx >= 0 && px + dx < Width && py + dy >= 0 && py + dy < Height)
										cellHeat[(py + dy) * Width + px + dx]++;
							}
						}
					}

					// Точки поверхности (центры треугольников): цвет одной и той же точки объекта от кадра к кадру.
					long pointTotal = 0, pointChanges = 0, pointFlicker = 0;

					for (int frame = 1; frame < Frames - 1; frame++)
					{
						foreach (var pair in points[frame])
						{
							if (!points[frame - 1].TryGetValue(pair.Key, out var before) || !points[frame + 1].TryGetValue(pair.Key, out var after))
								continue;

							pointTotal++;

							if (!Same(before, pair.Value))
								pointChanges++;

							if (Same(before, after) && !Same(before, pair.Value))
								pointFlicker++;
						}
					}

					// Зерно: «тонкие» пиксели — совпадают по цвету не больше чем с 1 из 4 соседей (пятнышко 1–2 px
					// или линия толщиной 1 px). При клетках от 3 px их быть не должно.
					long thin = 0;
					var thinHeat = new int[Width * Height];

					foreach (var frame in frames)
					{
						for (int y = 1; y < Height - 1; y++)
						for (int x = 1; x < Width - 1; x++)
						{
							int k = y * Width + x;
							int same = (Same(frame[k], frame[k - 1]) ? 1 : 0) + (Same(frame[k], frame[k + 1]) ? 1 : 0)
							         + (Same(frame[k], frame[k - Width]) ? 1 : 0) + (Same(frame[k], frame[k + Width]) ? 1 : 0);

							if (same <= 1)
							{
								thin++;
								thinHeat[k]++;
							}
						}
					}

					report.AppendLine($"t0={startTime}: ЗЕРНО (тонкие пиксели): {thin / (float)Frames:0} пикс/кадр");

					if (startTime == startTimes[0])
						SaveHeat(Path.Combine(OutputFolder, $"{label}_grain.png"), frames[Frames / 2], thinHeat);

					// Сравнение с эталоном: «лишний» пиксель — его цвета нет в эталоне в пределах клетки вокруг
					// (сдвиг клеток не считается, точки чужого цвета и следы — считаются).
					if (gtFrames.Count == frames.Count)
					{
						int radius = Mathf.RoundToInt(manager.Settings.CellPixels * Height / 720f) + 1;
						long wrong = 0, mismatch = 0;
						var mismatchHeat = new int[Width * Height];
						var wrongHeat = new int[Width * Height];
						int worstFrame = 0, worstCount = -1;

						for (int frame = 0; frame < frames.Count; frame++)
						{
							int count = CountWrong(frames[frame], gtFrames[frame], radius, wrongHeat);

							for (int i = 0; i < frames[frame].Length; i++)
							{
								if (excluded.Count > frame && excluded[frame].Contains(new Vector2Int(i % Width, i / Width)))
									continue;

								if (!Same(frames[frame][i], gtFrames[frame][i]))
								{
									mismatch++;
									mismatchHeat[i]++;
								}
							}

							wrong += count;

							if (count > worstCount)
							{
								worstCount = count;
								worstFrame = frame;
							}
						}

						report.AppendLine($"t0={startTime}: ОТЛИЧИЯ ОТ ЭТАЛОНА попиксельно: {mismatch / (float)frames.Count:0} пикс/кадр " +
						                  $"(при неподвижной камере должны быть ~0; при движении — сдвиг клеток между запеканиями)");

						if (startTime == startTimes[0])
							SaveHeat(Path.Combine(OutputFolder, $"{label}_mismatch.png"), frames[frames.Count - 1], mismatchHeat);

						report.AppendLine($"t0={startTime}: ЛИШНИЕ ПИКСЕЛИ (нет такого цвета в эталоне в радиусе {radius}): " +
						                  $"{wrong / (float)frames.Count:0} пикс/кадр, худший кадр {worstFrame}: {worstCount}");

						{
							string tag = startTime == startTimes[0] ? "" : "_b";
							var single = new int[Width * Height];
							CountWrong(frames[worstFrame], gtFrames[worstFrame], radius, single);
							SaveHeat(Path.Combine(OutputFolder, $"{label}_wrong{tag}.png"), frames[worstFrame], single);
							Save(Path.Combine(OutputFolder, $"{label}_worst_ours{tag}.png"), frames[worstFrame]);
							Save(Path.Combine(OutputFolder, $"{label}_worst_gt{tag}.png"), gtFrames[worstFrame]);
						}
					}

					report.AppendLine($"t0={startTime}: ТОЧКИ ПОВЕРХНОСТИ: {pointTotal / (Frames - 2)} шт/кадр, " +
					                  $"смены {100.0 * pointChanges / System.Math.Max(1, pointTotal):0.00}%, " +
					                  $"мигания A→B→A {100.0 * pointFlicker / System.Math.Max(1, pointTotal):0.00}%");
					report.AppendLine($"t0={startTime}: клетки объектов: {cellTotal / (Frames - 2)} шт/кадр, " +
					                  $"смены {100.0 * cellChanges / System.Math.Max(1, cellTotal):0.00}%, " +
					                  $"мигания A→B→A {100.0 * cellFlicker / System.Math.Max(1, cellTotal):0.00}%");

					// Тот же кадр ещё раз — рендер должен быть детерминированным.
					motion.Apply(startTime);
					int repeatDiff = Diff(frames[0], Render(manager, camera, screen, readback));

					var heat = new int[Width * Height];
					long changes = 0, flickers = 0;

					for (int frame = 1; frame < Frames; frame++)
						changes += Diff(frames[frame - 1], frames[frame]);

					for (int frame = 1; frame < Frames - 1; frame++)
					{
						Color32[] a = frames[frame - 1], b = frames[frame], c = frames[frame + 1];

						for (int i = 0; i < a.Length; i++)
						{
							if (Same(a[i], c[i]) && !Same(a[i], b[i]))
							{
								heat[i]++;
								flickers++;
							}
						}
					}

					report.AppendLine($"t0={startTime}: смены {changes / (float)(Frames - 1):0} пикс/кадр, " +
					                  $"мигания A→B→A {flickers / (float)(Frames - 2):0} пикс/кадр, повтор кадра: {repeatDiff} пикс");

					string suffix = startTime == startTimes[0] ? "" : "_b";

					// Метка с «gif» — 48 кадров подряд (кроп 400×300 по центру) для анимации.
					if (label.Contains("gif") && startTime == startTimes[0])
					{
						string folder = Path.Combine(OutputFolder, label + "_frames");
						Directory.CreateDirectory(folder);
						bool seq = label.Contains("seq"); // подряд идущие кадры во весь экран (для поиска миганий)
						bool full = seq || label.Contains("full");
						int cropWidth = full ? Width : 400, cropHeight = full ? Height : 300;
						int cx = (Width - cropWidth) / 2, cy = (Height - cropHeight) / 2;

						for (int frame = 0; frame < 48; frame++)
						{
							var crop = new Color32[cropWidth * cropHeight];

							for (int y = 0; y < cropHeight; y++)
								System.Array.Copy(frames[seq ? frame : full ? frame * (Frames - 1) / 47 : frame + 20], (cy + y) * Width + cx, crop, y * cropWidth, cropWidth);

							var texture = new Texture2D(cropWidth, cropHeight, TextureFormat.RGBA32, false);
							texture.SetPixels32(crop);
							texture.Apply();
							File.WriteAllBytes(Path.Combine(folder, $"{frame:000}.png"), texture.EncodeToPNG());

							// С эталоном — его кадры рядом, в папке *_gt.
							if (gtFrames.Count == frames.Count)
							{
								int source = full ? frame * (Frames - 1) / 47 : frame + 20;

								for (int y = 0; y < cropHeight; y++)
									System.Array.Copy(gtFrames[source], (cy + y) * Width + cx, crop, y * cropWidth, cropWidth);

								texture.SetPixels32(crop);
								texture.Apply();
								Directory.CreateDirectory(folder + "_gt");
								File.WriteAllBytes(Path.Combine(folder + "_gt", $"{frame:000}.png"), texture.EncodeToPNG());
							}

							Object.DestroyImmediate(texture);
						}
					}
					SaveHeat(Path.Combine(OutputFolder, $"{label}_cells{suffix}.png"), frames[Frames / 2], cellHeat);

					if (startTime == startTimes[0])
						SaveHeat(Path.Combine(OutputFolder, $"{label}_mixed.png"), frames[Frames / 2], _mixedHeat);
					SaveZoom(Path.Combine(OutputFolder, $"{label}_zoom{suffix}.png"), frames, cellHeat);
					SaveHeat(Path.Combine(OutputFolder, $"{label}_flicker{suffix}.png"), frames[Frames / 2], heat);
					Save(Path.Combine(OutputFolder, $"{label}_frame{suffix}.png"), frames[Frames / 2]);
				}

				int bakes = 0;

				foreach (var pixelObject in objects)
					bakes += pixelObject.BakeCount - (bakeCountsBefore.TryGetValue(pixelObject, out int before) ? before : 0);

				report.AppendLine($"запеканий сетки поверхностей: {PixelRenderManager.SurfaceBakes}");
				report.AppendLine($"запеканий за тест: {bakes} (объектов {objects.Length}); причины: ошибка в пикселях {PixelRenderObject.BakesByError}, " +
				                  $"пороги угла/масштаба {PixelRenderObject.BakesByThresholds}");
				report.AppendLine($"клетки с разным цветом в источнике: {100.0 * _cellsMixed / System.Math.Max(1, _cellsChecked):0.00}%");
				var st = manager.Settings;
				report.AppendLine($"настройки: Cell {st.CellPixels}, ScreenCell {st.ScreenCellPixels}, Auto {st.AutoFromTexels}, Integer {st.IntegerCells}, " +
				                  $"Bake {st.SurfaceBake}, Silhouette {st.BakeSilhouette}, Angle {st.BakeAngle}, Scale {st.BakeScale}, " +
				                  $"Dissolve {st.BakeDissolve}, Transition {st.Transition}, Hysteresis {manager.Hysteresis}");
				report.AppendLine($"objects: {objects.Length}, {Width}x{Height}, {Frames} кадров по {Step:0.0000} с");
			}
			catch (System.Exception exception)
			{
				report.AppendLine(exception.ToString());
			}
			finally
			{
				for (int i = 0; i < lights.Length; i++)
					lights[i].shadows = savedShadows[i];

				Shader.SetGlobalFloat("_PixelDebugUnlit", 0f);
				Shader.SetGlobalFloat("_PixelDebugPaths", 0f);
				PixelRenderObject.DisableMaskUpdates = false;
				manager.Settings.LiveSilhouette = savedLive;
				manager.Settings.SurfaceBake = savedBake;
				manager.Settings.Transition = savedTransition;
				manager.Settings.BakeSilhouette = savedSilhouette;
				manager.Settings.BakeAngle = savedAngle;
				manager.Settings.BakeScale = savedScale;
				manager.Settings.BakeDissolve = savedDissolve;
				manager.Settings.BakeMaxError = savedError;
				manager.Settings.EdgeHysteresis = savedEdge;

				foreach (var pixelObject in objects)
					pixelObject.Release();

				foreach (var capsule in capsules)
					Object.DestroyImmediate(capsule);

				manager.Release();
				camera.targetTexture = savedTarget;
				motion.Apply(0f);
				screen.Release();
				Object.DestroyImmediate(screen);
				Object.DestroyImmediate(readback);
				Object.DestroyImmediate(sourceReadback);
				File.WriteAllText(Path.Combine(OutputFolder, label + ".txt"), report.ToString());
			}
		}

		private static List<GameObject> SpawnPreviewCapsules()
		{
			var result = new List<GameObject>();
			var spawner = Object.FindFirstObjectByType<PixelRainSpawner>();

			if (spawner == null)
				return result;

			// Каждый запуск — одни и те же капсулы (сбрасываем внутреннее состояние спавнера).
			foreach (string field in new[] { "_random", "_cellOrder" })
				typeof(PixelRainSpawner).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(spawner, null);

			typeof(PixelRainSpawner).GetField("_spawned", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(spawner, 0);
			var random = new System.Random(3);

			for (int i = 0; i < 60; i++)
			{
				var capsule = spawner.Spawn(0.4f + (float)random.NextDouble() * 12f);

				if (capsule != null)
					result.Add(capsule);
			}

			return result;
		}

		// Цвет в центре каждой клетки сетки каждого объекта (якорь и клетка — как в PixelRenderObject.UpdateGrid).
		private static int[] ReadIds(RenderTexture source, Texture2D readback)
		{
			var previous = RenderTexture.active;
			RenderTexture.active = source;
			readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
			readback.Apply();
			RenderTexture.active = previous;
			var pixels = readback.GetPixels();
			_lastSource = pixels;
			var ids = new int[pixels.Length];

			for (int i = 0; i < pixels.Length; i++)
				ids[i] = pixels[i].a > 0f ? Mathf.FloorToInt(pixels[i].b / 4f + 1e-4f) : 0;

			return ids;
		}

		private static Dictionary<long, (Color32 color, int pixel)> SampleCells(Camera camera, PixelRenderObject[] objects, Color32[] frame, int[] ids)
		{
			var result = new Dictionary<long, (Color32 color, int pixel)>();
			float screenScale = Height / 720f;
			float focal = 0.5f * Height / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

			var shared = Object.FindFirstObjectByType<PixelRenderManager>()?.Settings;

			for (int index = 0; index < objects.Length; index++)
			{
				var pixelObject = objects[index];
				var settings = pixelObject.GetSettings(shared);
				var bounds = pixelObject.GetComponent<Renderer>().bounds;
				Vector2 anchor;
				float cell;
				int range;

				if (pixelObject.ScreenGrid || pixelObject.SurfaceGrid)
				{
					anchor = Vector2.zero;
					cell = Mathf.Max(1f, Mathf.Round(settings.ScreenCellPixels * screenScale));
					range = Mathf.CeilToInt(Width / cell) + 1;
				}
				else
				{
					Vector3 point = camera.WorldToScreenPoint(bounds.center);

					if (point.z <= camera.nearClipPlane || settings.AutoFromTexels)
						continue;

					anchor = point;
					cell = Mathf.Max(1f, Mathf.Round(settings.CellPixels * screenScale));
					float radius = bounds.extents.magnitude;
					range = Mathf.CeilToInt(radius * focal / Mathf.Max(point.z - radius, camera.nearClipPlane) / cell) + 1;
				}

				int from = pixelObject.ScreenGrid || pixelObject.SurfaceGrid ? 0 : -range;

				for (int j = from; j < range; j++)
				for (int i = from; i < range; i++)
				{
					int x = Mathf.FloorToInt(anchor.x + (i + 0.5f) * cell), y = Mathf.FloorToInt(anchor.y + (j + 0.5f) * cell);

					if (x < 0 || y < 0 || x >= Width || y >= Height)
						continue;

					// Только клетки, центр которых в источнике — этот объект.
					if (ids[y * Width + x] != pixelObject.Id)
						continue;

					// Согласованность источника: все пиксели объекта внутри клетки должны иметь один цвет.
					{
						int cx0 = Mathf.CeilToInt(anchor.x + i * cell - 0.5f), cx1 = Mathf.CeilToInt(anchor.x + (i + 1) * cell - 0.5f);
						int cy0 = Mathf.CeilToInt(anchor.y + j * cell - 0.5f), cy1 = Mathf.CeilToInt(anchor.y + (j + 1) * cell - 0.5f);
						Color reference = _lastSource[y * Width + x];
						bool mixed = false;

						for (int yy = Mathf.Max(0, cy0); yy < Mathf.Min(Height, cy1) && !mixed; yy++)
						for (int xx = Mathf.Max(0, cx0); xx < Mathf.Min(Width, cx1); xx++)
						{
							int k = yy * Width + xx;

							if (ids[k] != pixelObject.Id)
								continue;

							Color other = _lastSource[k];

							if (Mathf.Abs(other.r - reference.r) + Mathf.Abs(other.g - reference.g) + Mathf.Abs(other.b - reference.b) > 0.02f)
							{
								mixed = true;
								break;
							}
						}

						_cellsChecked++;

						if (mixed)
						{
							_cellsMixed++;

							for (int dy = -2; dy <= 2; dy++)
							for (int dx = -2; dx <= 2; dx++)
								if (x + dx >= 0 && x + dx < Width && y + dy >= 0 && y + dy < Height)
									_mixedHeat[(y + dy) * Width + x + dx]++;
						}
					}

					Color32 c = frame[y * Width + x];

					result[((long)index << 40) | ((long)(i + 100000) << 20) | (long)(j + 100000) & 0xFFFFF] = (c, y * Width + x);
				}
			}

			return result;
		}

		/// <summary>
		/// Честный эталон кадра. Для каждого объекта: рендер источника, где виден только он (никто не перекрывает —
		/// нечего угадывать), его сетка (якорь — центр рамки на экране, клетка из настроек; пол — экранная сетка),
		/// клетка занята, если центр клетки покрыт объектом (билинейно по 4 пикселям ≥ 0.5), цвет и глубина — пиксель
		/// объекта у центра. Объекты складываются по глубине клеток. Никаких эвристик сборки.
		/// </summary>
		private static Color32[] RenderTruth(PixelRenderManager manager, Camera camera, PixelRenderObject[] objects, Texture2D sourceReadback)
		{
			var result = new Color32[Width * Height];
			var zbuffer = new float[Width * Height];

			for (int i = 0; i < zbuffer.Length; i++)
			{
				zbuffer[i] = float.MaxValue;
				result[i] = new Color32(0, 0, 0, 255);
			}

			var settings = manager.Settings;
			bool savedBake = settings.SurfaceBake, savedHysteresis = manager.Hysteresis;
			settings.SurfaceBake = false;
			manager.Hysteresis = false;
			PixelRenderManager.SuppressCaptures = true;
			var renderers = new MeshRenderer[objects.Length];

			for (int i = 0; i < objects.Length; i++)
				renderers[i] = objects[i].GetComponent<MeshRenderer>();

			float screenScale = Height / 720f;
			float focal = 0.5f * Height / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

			try
			{
				for (int index = 0; index < objects.Length; index++)
				{
					var pixelObject = objects[index];
					var objectSettings = pixelObject.GetSettings(settings);
					Bounds bounds = renderers[index].bounds;
					Vector2 anchor;
					float cell;
					int x0, y0, x1, y1;

					if (pixelObject.ScreenGrid || pixelObject.SurfaceGrid)
					{
						anchor = Vector2.zero;
						cell = Mathf.Max(1f, Mathf.Round(objectSettings.ScreenCellPixels * screenScale));
						x0 = 0; y0 = 0; x1 = Width; y1 = Height;
					}
					else
					{
						Vector3 point = camera.WorldToScreenPoint(bounds.center);

						if (point.z <= camera.nearClipPlane)
							continue;

						anchor = point;
						cell = Mathf.Max(1f, Mathf.Round(objectSettings.CellPixels * screenScale));
						float radius = bounds.extents.magnitude;
						float screenRadius = radius * focal / Mathf.Max(point.z - radius, camera.nearClipPlane) + cell * 2f;
						x0 = Mathf.Max(0, (int)(anchor.x - screenRadius)); y0 = Mathf.Max(0, (int)(anchor.y - screenRadius));
						x1 = Mathf.Min(Width, (int)(anchor.x + screenRadius) + 1); y1 = Mathf.Min(Height, (int)(anchor.y + screenRadius) + 1);

						if (x1 <= x0 || y1 <= y0)
							continue;
					}

					for (int i = 0; i < renderers.Length; i++)
						renderers[i].enabled = i == index;

					manager.RenderSourceNow();
					var previous = RenderTexture.active;
					RenderTexture.active = manager.SourceTexture;
					sourceReadback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
					sourceReadback.Apply();
					RenderTexture.active = previous;
					var source = sourceReadback.GetPixels();
					int id = pixelObject.Id;

					bool Own(int x, int y, out Color value)
					{
						x = Mathf.Clamp(x, 0, Width - 1);
						y = Mathf.Clamp(y, 0, Height - 1);
						value = source[y * Width + x];
						return value.a > 0f && Mathf.FloorToInt(value.b / 4f + 1e-4f) == id;
					}

					int i0 = Mathf.FloorToInt((x0 - anchor.x) / cell) - 1, i1 = Mathf.CeilToInt((x1 - anchor.x) / cell) + 1;
					int j0 = Mathf.FloorToInt((y0 - anchor.y) / cell) - 1, j1 = Mathf.CeilToInt((y1 - anchor.y) / cell) + 1;

					for (int j = j0; j < j1; j++)
					for (int i = i0; i < i1; i++)
					{
						Vector2 center = anchor + new Vector2(i + 0.5f, j + 0.5f) * cell;
						Vector2 corner = center - new Vector2(0.5f, 0.5f);
						int cx = Mathf.FloorToInt(corner.x), cy = Mathf.FloorToInt(corner.y);
						float fx = corner.x - cx, fy = corner.y - cy;
						float own = 0f, bestWeight = -1f;
						Color best = default;

						for (int k = 0; k < 4; k++)
						{
							int ox = k & 1, oy = k >> 1;
							float weight = (ox == 0 ? 1f - fx : fx) * (oy == 0 ? 1f - fy : fy);

							if (Own(cx + ox, cy + oy, out var value))
							{
								own += weight;

								if (weight > bestWeight)
								{
									bestWeight = weight;
									best = value;
								}
							}
						}

						if (own < 0.5f)
							continue;

						float b = best.b - Mathf.Floor(best.b / 4f + 1e-4f) * 4f;
						b -= b >= 2f ? 2f : 0f;
						var color = (Color32)new Color(best.r, best.g, b, 1f).gamma;
						int px0 = Mathf.CeilToInt(anchor.x + i * cell - 0.5f), px1 = Mathf.CeilToInt(anchor.x + (i + 1) * cell - 0.5f);
						int py0 = Mathf.CeilToInt(anchor.y + j * cell - 0.5f), py1 = Mathf.CeilToInt(anchor.y + (j + 1) * cell - 0.5f);

						for (int y = Mathf.Max(0, py0); y < Mathf.Min(Height, py1); y++)
						for (int x = Mathf.Max(0, px0); x < Mathf.Min(Width, px1); x++)
						{
							int k = y * Width + x;

							if (best.a < zbuffer[k])
							{
								zbuffer[k] = best.a;
								result[k] = color;
							}
						}
					}
				}
			}
			finally
			{
				foreach (var renderer in renderers)
					renderer.enabled = true;

				settings.SurfaceBake = savedBake;
				manager.Hysteresis = savedHysteresis;
				PixelRenderManager.SuppressCaptures = false;
			}

			return result;
		}

		/// <summary>Пиксели «наших» кадров, цвета которых нет в эталоне в квадрате ±radius.</summary>
		private static int CountWrong(Color32[] ours, Color32[] gt, int radius, int[] heat)
		{
			// Для каждого цвета эталона — карта «цвет есть в окне ±radius» (раздельный максимум по строкам и столбцам).
			var colorIndex = new Dictionary<int, int>();
			var gtIndex = new int[gt.Length];
			int canonicalCount = 0;

			for (int i = 0; i < gt.Length; i++)
			{
				int key = gt[i].r << 16 | gt[i].g << 8 | gt[i].b;

				if (!colorIndex.TryGetValue(key, out int index))
				{
					// Близкие цвета (отличаются на 1–2 из-за конвертации) — один цвет палитры.
					index = -1;

					foreach (var pair in colorIndex)
					{
						var c = new Color32((byte)(pair.Key >> 16), (byte)(pair.Key >> 8), (byte)pair.Key, 255);

						if (Same(c, gt[i]))
						{
							index = pair.Value;
							break;
						}
					}

					if (index < 0)
						index = canonicalCount++;

					colorIndex[key] = index;
				}

				gtIndex[i] = index;
			}

			// Наш цвет → ближайший цвет палитры эталона (с допуском: эталон собирается на CPU, байты могут
			// отличаться на 1–2).
			var palette = new List<Color32>();

			foreach (var pair in colorIndex)
				palette.Add(new Color32((byte)(pair.Key >> 16), (byte)(pair.Key >> 8), (byte)pair.Key, 255));

			var paletteIndex = new int[palette.Count];

			foreach (var pair in colorIndex)
				paletteIndex[pair.Value] = pair.Value;

			var ourIndex = new int[ours.Length];
			var ourCache = new Dictionary<int, int>();

			for (int i = 0; i < ours.Length; i++)
			{
				int key = ours[i].r << 16 | ours[i].g << 8 | ours[i].b;

				if (!ourCache.TryGetValue(key, out int index))
				{
					index = -1;

					foreach (var pair in colorIndex)
					{
						var c = new Color32((byte)(pair.Key >> 16), (byte)(pair.Key >> 8), (byte)pair.Key, 255);

						if (Same(c, ours[i]))
						{
							index = pair.Value;
							break;
						}
					}

					ourCache[key] = index;
				}

				ourIndex[i] = index;
			}

			int count = 0;
			var near = new bool[gt.Length];
			var row = new int[gt.Length];

			for (int color = 0; color < canonicalCount; color++)
			{

				// Сумма по строке в окне, затем по столбцу — «есть ли цвет в квадрате».
				for (int y = 0; y < Height; y++)
				{
					int sum = 0, offset = y * Width;

					for (int x = -radius; x < Width; x++)
					{
						if (x + radius < Width && gtIndex[offset + x + radius] == color) sum++;
						if (x - radius - 1 >= 0 && gtIndex[offset + x - radius - 1] == color) sum--;
						if (x >= 0) row[offset + x] = sum;
					}
				}

				for (int x = 0; x < Width; x++)
				{
					int sum = 0;

					for (int y = -radius; y < Height; y++)
					{
						if (y + radius < Height) sum += row[(y + radius) * Width + x];
						if (y - radius - 1 >= 0) sum -= row[(y - radius - 1) * Width + x];

						if (y >= 0)
						{
							int k = y * Width + x;
							if (sum > 0 && ourIndex[k] == color)
								near[k] = true;
						}
					}
				}
			}

			for (int i = 0; i < ours.Length; i++)
			{
				if (!near[i])
				{
					count++;
					heat[i]++;
				}
			}

			return count;
		}

		private static Dictionary<Mesh, Vector3[]> _centroids = new();

		private static Dictionary<long, Color32> SamplePoints(Camera camera, PixelRenderObject[] objects, Color32[] frame, int[] ids)
		{
			var result = new Dictionary<long, Color32>();
			var depths = _lastSource;

			for (int index = 0; index < objects.Length; index++)
			{
				var pixelObject = objects[index];

				if (pixelObject.ScreenGrid || pixelObject.SurfaceGrid)
					continue;

				var mesh = pixelObject.GetComponent<MeshFilter>().sharedMesh;

				if (!_centroids.TryGetValue(mesh, out var centroids))
				{
					var vertices = mesh.vertices;
					var triangles = mesh.triangles;
					centroids = new Vector3[triangles.Length / 3];

					for (int t = 0; t < centroids.Length; t++)
						centroids[t] = (vertices[triangles[t * 3]] + vertices[triangles[t * 3 + 1]] + vertices[triangles[t * 3 + 2]]) / 3f;

					_centroids[mesh] = centroids;
				}

				var matrix = pixelObject.transform.localToWorldMatrix;

				for (int t = 0; t < centroids.Length; t++)
				{
					Vector3 screen = camera.WorldToScreenPoint(matrix.MultiplyPoint3x4(centroids[t]));
					int x = Mathf.FloorToInt(screen.x), y = Mathf.FloorToInt(screen.y);

					if (screen.z <= 0f || x < 0 || y < 0 || x >= Width || y >= Height)
						continue;

					int k = y * Width + x;

					// Точка видна: в источнике тут этот объект на той же глубине.
					if (ids[k] != pixelObject.Id || Mathf.Abs(depths[k].a - screen.z) > 0.02f * screen.z)
						continue;

					// Только точки внутри однотонного пятна 5×5: смена цвета здесь — это смена целого «пикселя»
					// объекта, а не обычное ползание края на 1 пиксель.
					if (x < 2 || y < 2 || x >= Width - 2 || y >= Height - 2)
						continue;

					bool uniform = true;

					for (int dy = -2; dy <= 2 && uniform; dy++)
					for (int dx = -2; dx <= 2; dx++)
					{
						if (!Same(frame[k + dy * Width + dx], frame[k]))
						{
							uniform = false;
							break;
						}
					}

					if (uniform)
						result[((long)index << 32) | (uint)t] = frame[k];
				}
			}

			return result;
		}

		private static Color32[] Render(PixelRenderManager manager, Camera camera, RenderTexture screen, Texture2D readback)
		{
			manager.RenderSourceNow();
			RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = screen });
			var previous = RenderTexture.active;
			RenderTexture.active = screen;
			readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
			readback.Apply();
			RenderTexture.active = previous;
			return readback.GetPixels32();
		}

		private static bool Same(Color32 a, Color32 b) =>
			Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) <= 6;

		private static int Diff(Color32[] a, Color32[] b)
		{
			int count = 0;

			for (int i = 0; i < a.Length; i++)
				if (!Same(a[i], b[i]))
					count++;

			return count;
		}

		private static void Save(string path, Color32[] pixels)
		{
			var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
			texture.SetPixels32(pixels);
			texture.Apply();
			File.WriteAllBytes(path, texture.EncodeToPNG());
			Object.DestroyImmediate(texture);
		}

		// Самое мигающее место: 48×48 вокруг него в 5 кадрах подряд (×6), в кадре, где оно мигает.
		private static void SaveZoom(string path, List<Color32[]> frames, int[] heat)
		{
			const int size = 48, scale = 6, count = 5;
			int best = 0;

			for (int i = 0; i < heat.Length; i++)
				if (heat[i] > heat[best])
					best = i;

			int bx = best % Width, by = best / Width;
			int middle = 2;

			for (int frame = 1; frame < frames.Count - 1; frame++)
			{
				Color32 a = frames[frame - 1][best], b = frames[frame][best], c = frames[frame + 1][best];

				if (Same(a, c) && !Same(a, b))
				{
					middle = Mathf.Clamp(frame, 2, frames.Count - 3);
					break;
				}
			}

			int x0 = Mathf.Clamp(bx - size / 2, 0, Width - size), y0 = Mathf.Clamp(by - size / 2, 0, Height - size);
			int outWidth = (size * scale + 4) * count, outHeight = size * scale;
			var pixels = new Color32[outWidth * outHeight];

			for (int k = 0; k < count; k++)
			{
				var frame = frames[middle - 2 + k];

				for (int y = 0; y < outHeight; y++)
				for (int x = 0; x < size * scale; x++)
				{
					int sx = x0 + x / scale, sy = y0 + y / scale;
					Color32 c = frame[sy * Width + sx];

					if (sx == bx && sy == by && (x % scale == 0 || y % scale == 0))
						c = new Color32(255, 0, 255, 255);

					pixels[y * outWidth + k * (size * scale + 4) + x] = c;
				}
			}

			var texture = new Texture2D(outWidth, outHeight, TextureFormat.RGBA32, false);
			texture.SetPixels32(pixels);
			texture.Apply();
			File.WriteAllBytes(path, texture.EncodeToPNG());
			Object.DestroyImmediate(texture);
		}

		// Кадр приглушён, мигающие пиксели — красным (ярче — чаще).
		private static void SaveHeat(string path, Color32[] frame, int[] heat)
		{
			var pixels = new Color32[frame.Length];

			for (int i = 0; i < frame.Length; i++)
			{
				Color32 c = frame[i];
				byte gray = (byte)((c.r + c.g + c.b) / 9);

				pixels[i] = heat[i] > 0
					? new Color32((byte)Mathf.Min(255, 120 + heat[i] * 20), (byte)Mathf.Min(255, heat[i] * 6), 0, 255)
					: new Color32(gray, gray, gray, 255);
			}

			Save(path, pixels);
		}
	}
}
