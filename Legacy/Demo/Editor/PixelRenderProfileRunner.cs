using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EliotByte.PixelRender.Legacy.Demo.Editor
{
	/// <summary>
	/// Замер производительности демо-сцены: меню Tools/Pixel Render/Profile Demo Scene или файл
	/// PixelRenderProfile.request в корне проекта. Открывает демо-сцену, входит в Play, добавляет PixelRenderProfiler,
	/// выходит из Play, когда замер готов → SandboxCaptures/PixelRender/profile.txt.
	/// </summary>
	[InitializeOnLoad]
	public static class PixelRenderProfileRunner
	{
		private const string Request = "PixelRenderProfile.request";
		private const string StateKey = "EliotByte.PixelRender.Profiling";
		private const string StartKey = "EliotByte.PixelRender.ProfilingStart";
		private static double _nextPoll;

		static PixelRenderProfileRunner()
		{
			EditorApplication.update -= Poll;
			EditorApplication.update += Poll;
			EditorApplication.playModeStateChanged -= OnPlayMode;
			EditorApplication.playModeStateChanged += OnPlayMode;
		}

		[MenuItem("Tools/Pixel Render/Legacy/Profile Demo Scene")]
		public static void Run()
		{
			if (EditorApplication.isPlayingOrWillChangePlaymode)
				return;

			if (SceneManager.GetActiveScene().isDirty && SceneManager.GetActiveScene().path != PixelRenderSceneCreator.ScenePath)
			{
				Directory.CreateDirectory("SandboxCaptures/PixelRender");
				File.WriteAllText("SandboxCaptures/PixelRender/profile.txt", "SKIPPED: открытая сцена не сохранена.");
				return;
			}

			if (SceneManager.GetActiveScene().path != PixelRenderSceneCreator.ScenePath)
				EditorSceneManager.OpenScene(PixelRenderSceneCreator.ScenePath, OpenSceneMode.Single);

			SessionState.SetBool(StateKey, true);
			SessionState.SetFloat(StartKey, (float)EditorApplication.timeSinceStartup);
			EditorApplication.isPlaying = true;
		}

		private static void OnPlayMode(PlayModeStateChange change)
		{
			if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(StateKey, false))
			{
				PixelRenderProfiler.Done = false;
				new GameObject("Pixel Render Profiler").AddComponent<PixelRenderProfiler>();
			}

			if (change == PlayModeStateChange.EnteredEditMode)
				SessionState.SetBool(StateKey, false);
		}

		private static void Poll()
		{
			if (EditorApplication.isPlaying && SessionState.GetBool(StateKey, false))
			{
				bool timeout = EditorApplication.timeSinceStartup - SessionState.GetFloat(StartKey, 0f) > 600.0;

				if (PixelRenderProfiler.Done || timeout)
				{
					if (timeout && !PixelRenderProfiler.Done)
						File.WriteAllText("SandboxCaptures/PixelRender/profile.txt", "TIMEOUT: замер не уложился в 10 минут.");

					EditorApplication.isPlaying = false;
				}

				return;
			}

			if (EditorApplication.timeSinceStartup < _nextPoll)
				return;

			_nextPoll = EditorApplication.timeSinceStartup + 1.0;

			if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
				return;

			// Подхватить изменённые скрипты до замера (иначе в фоне Unity гоняет старую сборку).
			AssetDatabase.Refresh();

			if (EditorApplication.isCompiling || EditorApplication.isUpdating)
				return;

			File.Delete(Request);
			Run();
		}
	}
}
