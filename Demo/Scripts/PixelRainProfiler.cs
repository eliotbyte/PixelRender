using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace EliotByte.PixelRender.Demo
{
	/// <summary>
	/// Замер производительности дождя капсул (добавляется редактором в Play, запрос «rainprofile»): капсулы сыплются
	/// быстро, на каждой отметке Milestones дождь останавливается, после паузы снимается SampleFrames кадров.
	/// </summary>
	public class PixelRainProfiler : MonoBehaviour
	{
		public int[] Milestones = { 0, 250, 1000 };
		public int SettleFrames = 200;
		public int SampleFrames = 120;
		public float SpawnInterval = 0.004f;
		public string OutputPath = "SandboxCaptures/PixelRender/rainprofile.txt";

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
		private readonly FrameTiming[] _timings = new FrameTiming[1];
		private PixelRainSpawner _rain;
		private int _milestone;
		private int _frame;
		private bool _sampling;
		private double _gpuSum, _cpuSum;
		private int _gpuFrames;

		// Счётчики окна Stats редактора (UnityStats): у ProfilerRecorder в редакторе Draw Calls/Batches = 0.
		private static readonly string[] StatNames = { "drawCalls", "batches", "setPassCalls", "triangles", "vertices", "shadowCasters", "renderTextureChanges", "usedTextureMemorySize" };
		private readonly double[] _statSums = new double[StatNames.Length];
		private static readonly System.Type StatsType = System.Type.GetType("UnityEditor.UnityStats, UnityEditor");

		private static double Stat(string name)
		{
			var property = StatsType?.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			return property != null ? System.Convert.ToDouble(property.GetValue(null)) : -1;
		}

		private void Add(string label, ProfilerCategory category, string name, bool isTime) =>
			_metrics.Add(new Metric { Label = label, Recorder = ProfilerRecorder.StartNew(category, name), IsTime = isTime });

		private void Start()
		{
			Done = false;
			Application.runInBackground = true;
			Application.logMessageReceived += OnLog;
			_rain = FindFirstObjectByType<PixelRainSpawner>();

			if (_rain != null)
			{
				_rain.Interval = SpawnInterval;
				_rain.Total = Mathf.Max(_rain.Total, Milestones[^1]);
			}

			Add("Main Thread, мс", ProfilerCategory.Internal, "Main Thread", true);
			Add("Render Thread, мс", ProfilerCategory.Internal, "Render Thread", true);
			Add("GPU (recorder), мс", ProfilerCategory.Render, "GPU Frame Time", true);
			Add("PixelRender.Step, мс", ProfilerCategory.Scripts, "PixelRender.Step", true);
			Add("Физика (Physics.Simulate), мс", ProfilerCategory.Physics, "Physics.Simulate", true);
			Add("Draw Calls", ProfilerCategory.Render, "Draw Calls Count", false);
			Add("Batches", ProfilerCategory.Render, "Batches Count", false);
			Add("SetPass Calls", ProfilerCategory.Render, "SetPass Calls Count", false);
			Add("Triangles", ProfilerCategory.Render, "Triangles Count", false);

			_report.AppendLine($"Экран {Screen.width}×{Screen.height}, Unity {Application.unityVersion}, {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), " +
			                   $"{SystemInfo.processorType}. В редакторе (Play), среднее за {SampleFrames} кадров.");
			_report.AppendLine();
		}

		private readonly HashSet<string> _logs = new();

		private void OnLog(string message, string stack, LogType type)
		{
			if (type != LogType.Log && _logs.Count < 20)
				_logs.Add(type + ": " + message);
		}

		private void OnDestroy()
		{
			Application.logMessageReceived -= OnLog;
			foreach (var metric in _metrics)
				metric.Recorder.Dispose();
		}

		private void Update()
		{
			if (Done)
				return;

			if (_rain == null)
			{
				_report.AppendLine("нет PixelRainSpawner в сцене");
				Finish();
				return;
			}

			if (!_sampling)
			{
				if (_rain.Count < Milestones[_milestone])
					return;

				_rain.Paused = true;

				if (++_frame < SettleFrames)
					return;

				_sampling = true;
				_frame = 0;

				for (int i = 0; i < _metrics.Count; i++)
				{
					var m = _metrics[i];
					m.Sum = 0;
					_metrics[i] = m;
				}

				_gpuSum = _cpuSum = 0;
				_gpuFrames = 0;
				System.Array.Clear(_statSums, 0, _statSums.Length);
				return;
			}

			for (int i = 0; i < _metrics.Count; i++)
			{
				var m = _metrics[i];
				m.Sum += m.Recorder.Valid ? m.Recorder.LastValue : 0;
				_metrics[i] = m;
			}

			for (int i = 0; i < StatNames.Length; i++)
				_statSums[i] += Stat(StatNames[i]);

			FrameTimingManager.CaptureFrameTimings();

			if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
			{
				_gpuSum += _timings[0].gpuFrameTime;
				_cpuSum += _timings[0].cpuFrameTime;
				_gpuFrames++;
			}

			if (++_frame < SampleFrames)
				return;

			_report.AppendLine($"== капсул {_rain.Count}, область {_rain.Area}");
			var spread = new Bounds();
			bool first = true;

			foreach (Transform child in _rain.transform)
			{
				if (child.name != "Capsule") continue;
				if (first) spread = new Bounds(child.position, Vector3.zero); else spread.Encapsulate(child.position);
				first = false;
			}

			_report.AppendLine($"  капсулы лежат в X {spread.min.x:0}…{spread.max.x:0}, Z {spread.min.z:0}…{spread.max.z:0}, Y {spread.min.y:0}…{spread.max.y:0}");

			foreach (var m in _metrics)
			{
				double value = m.Sum / SampleFrames;
				_report.AppendLine($"  {m.Label}: {(!m.Recorder.Valid ? "нет данных" : m.IsTime ? $"{value / 1e6:0.00}" : $"{value:0}")}");
			}

			if (_gpuFrames > 0)
				_report.AppendLine($"  FrameTiming: CPU {_cpuSum / _gpuFrames:0.00} мс, GPU {_gpuSum / _gpuFrames:0.00} мс");

			_report.AppendLine("  Stats (UnityStats): " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, StatNames.Length), i => $"{StatNames[i]} {_statSums[i] / SampleFrames:0}")));
			_report.AppendLine();
			_sampling = false;
			_frame = 0;

			ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(OutputPath), $"rain_{_rain.Count}.png"));

			if (++_milestone >= Milestones.Length)
			{
				Finish();
				return;
			}

			_rain.Paused = false;
		}

		private void Finish()
		{
			foreach (string log in _logs)
				_report.AppendLine(log);

			Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
			File.WriteAllText(OutputPath, _report.ToString());
			Done = true;
		}
	}
}
