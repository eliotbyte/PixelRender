using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace EliotByte.PixelRender.Legacy.Demo
{
	/// <summary>
	/// Замер производительности демо-сцены (добавляет PixelRenderProfileRunner в Play): капсулы сыплются быстро,
	/// на каждой отметке Milestones дождь останавливается, после паузы снимается SampleFrames кадров —
	/// средние времена CPU/GPU, вызовы отрисовки, физика и время самого пиксельного рендера. Итог — OutputPath.
	/// </summary>
	public class PixelRenderProfiler : MonoBehaviour
	{
		public int[] Milestones = { 0, 250, 1000 };
		public int SettleFrames = 150; // капсулам нужно ~2 с, чтобы долететь до пола и попасть в кадр
		public int SampleFrames = 120;
		public float SpawnInterval = 0.004f;
		public string OutputPath = "SandboxCaptures/PixelRender/profile.txt";

		/// <summary>Замер закончен (редактор выходит из Play).</summary>
		public static bool Done;

		private struct Metric
		{
			public string Label;
			public ProfilerRecorder Recorder;
			public bool IsTime;
			public double Sum;
		}

		private readonly List<Metric> _metrics = new();
		private readonly StringBuilder _report = new();
		private PixelRainSpawner _spawner;
		private int _milestone;
		private int _frame;
		private bool _sampling;
		private double _gpuSum, _cpuSum;
		private int _gpuFrames;
		private readonly FrameTiming[] _timings = new FrameTiming[1];

		private static readonly string[] Experiments =
		{
			"без дозаписи масок объектов",
			"без сборки объектов (квады объектов не рисуются)",
			"без сборки пола",
			"без всей сборки (остаётся источник)"
		};

		private int _experiment;

		private static void SetExperiment(int index)
		{
			PixelRenderObject.DisableMaskUpdates = index == 0;
			PixelRenderObject.DebugSkipResolve = index == 1 || index == 3;
			PixelRenderObject.DebugSkipSurfaceResolve = index == 2 || index == 3;
		}

		private void Add(string label, ProfilerCategory category, string name, bool isTime)
		{
			_metrics.Add(new Metric { Label = label, Recorder = ProfilerRecorder.StartNew(category, name), IsTime = isTime });
		}

		private void Start()
		{
			Done = false;
			SetExperiment(-1);
			Application.runInBackground = true;
			_spawner = FindFirstObjectByType<PixelRainSpawner>();

			if (_spawner != null)
			{
				_spawner.Interval = SpawnInterval;
				_spawner.Total = Mathf.Max(_spawner.Total, Milestones[^1]);
			}

			Add("Main Thread, мс", ProfilerCategory.Internal, "Main Thread", true);
			Add("Render Thread, мс", ProfilerCategory.Internal, "Render Thread", true);
			Add("GPU (recorder), мс", ProfilerCategory.Render, "GPU Frame Time", true);
			Add("PixelRender.Step, мс", ProfilerCategory.Scripts, "PixelRender.Step", true);
			Add("  из них UpdateGrids, мс", ProfilerCategory.Scripts, "PixelRender.UpdateGrids", true);
			Add("    прямоугольник (Rect), мс", ProfilerCategory.Scripts, "PixelRender.Grid.Rect", true);
			Add("    запекание (Bake), мс", ProfilerCategory.Scripts, "PixelRender.Grid.Bake", true);
			Add("    блоки свойств (Blocks), мс", ProfilerCategory.Scripts, "PixelRender.Grid.Blocks", true);
			Add("    квад (Quad), мс", ProfilerCategory.Scripts, "PixelRender.Grid.Quad", true);
			Add("PixelRender.RecordCaptures, мс", ProfilerCategory.Scripts, "PixelRender.RecordCaptures", true);
			Add("Физика (Physics.Simulate), мс", ProfilerCategory.Physics, "Physics.Simulate", true);
			Add("Draw Calls", ProfilerCategory.Render, "Draw Calls Count", false);
			Add("Batches", ProfilerCategory.Render, "Batches Count", false);
			Add("SetPass Calls", ProfilerCategory.Render, "SetPass Calls Count", false);
			Add("Triangles", ProfilerCategory.Render, "Triangles Count", false);

			_report.AppendLine($"Экран {Screen.width}×{Screen.height}, Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), " +
			                   $"{SystemInfo.processorType}. В редакторе (Play), времена — среднее за {SampleFrames} кадров.");
			_report.AppendLine("GPU (FrameTiming) — время GPU по FrameTimingManager (0 — не поддерживается).");
			_report.AppendLine();
		}

		private void OnDestroy()
		{
			SetExperiment(-1);
			for (int i = 0; i < _metrics.Count; i++)
				_metrics[i].Recorder.Dispose();
		}

		private void Update()
		{
			if (Done || _spawner == null)
				return;

			if (!_sampling)
			{
				if (_spawner.Count < Milestones[_milestone])
					return;

				_spawner.enabled = false; // дождь на паузе: замер без новых капсул
				_frame++;

				if (_frame < SettleFrames)
					return;

				_sampling = true;
				_frame = 0;
				ResetSums();
				return;
			}

			for (int i = 0; i < _metrics.Count; i++)
			{
				var metric = _metrics[i];
				metric.Sum += metric.Recorder.Valid ? metric.Recorder.LastValue : 0;
				_metrics[i] = metric;
			}

			FrameTimingManager.CaptureFrameTimings();

			if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
			{
				_gpuSum += _timings[0].gpuFrameTime;
				_cpuSum += _timings[0].cpuFrameTime;
				_gpuFrames++;
			}

			if (++_frame < SampleFrames)
				return;

			Write();
			_sampling = false;
			_frame = 0;
			_milestone++;

			// После последней отметки — опыты: то же самое, но без одной из частей (сколько она стоит).
			if (_milestone == Milestones.Length && _experiment < Experiments.Length)
			{
				SetExperiment(_experiment);
				_report.AppendLine($"== опыт: {Experiments[_experiment]}");
				_experiment++;
				_milestone--;
				_sampling = false;
				_frame = SettleFrames - 10;
				return;
			}

			if (_milestone >= Milestones.Length)
			{
				SetExperiment(-1);
				Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
				File.WriteAllText(OutputPath, _report.ToString());
				Done = true;
				return;
			}

			_spawner.enabled = true;
		}

		private void ResetSums()
		{
			for (int i = 0; i < _metrics.Count; i++)
			{
				var metric = _metrics[i];
				metric.Sum = 0;
				_metrics[i] = metric;
			}

			_gpuSum = _cpuSum = 0;
			_gpuFrames = 0;
		}

		private void Write()
		{
			int objects = FindObjectsByType<PixelRenderObject>(FindObjectsSortMode.None).Length;
			_report.AppendLine($"== капсул {_spawner.Count}, пиксельных объектов {objects}");

			foreach (var metric in _metrics)
			{
				double value = metric.Sum / SampleFrames;
				string text = !metric.Recorder.Valid ? "нет данных" : metric.IsTime ? $"{value / 1e6:0.00}" : $"{value:0}";
				_report.AppendLine($"  {metric.Label}: {text}");
			}

			_report.AppendLine(_gpuFrames > 0
				? $"  GPU (FrameTiming), мс: {_gpuSum / _gpuFrames:0.00};  CPU (FrameTiming), мс: {_cpuSum / _gpuFrames:0.00}"
				: "  GPU (FrameTiming): нет данных");
		}
	}
}
