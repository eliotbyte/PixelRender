using UnityEngine;

namespace EliotByte.PixelRender.Legacy.Demo
{
	/// <summary>
	/// Камера сцены PixelRender без управления: всегда смотрит в центр шара, очень медленно облетает его
	/// по часовой стрелке (если смотреть сверху), по синусу чуть приближается/отдаляется (~1 м)
	/// и с маленькой амплитудой поднимается/опускается.
	/// </summary>
	[DefaultExecutionOrder(-100)]
	public class PixelRenderCameraMotion : MonoBehaviour
	{
		public Vector3 Center = new(0f, 2.2f, 0f);
		[Tooltip("Среднее расстояние до центра шара.")]
		public float Distance = 9.75f;
		[Tooltip("Наклон взгляда вниз, градусы.")]
		public float Pitch = 35f;

		[Header("Облёт (по часовой стрелке)")]
		public float StartYaw = 25f;
		[Tooltip("Градусов в секунду.")]
		public float OrbitSpeed = 3f;

		[Header("Приближение / отдаление")]
		[Tooltip("±метры: 9.75 ± 1.85 → от ~7.9 до ~11.6 м (шар занимает ~60% и ~40% высоты кадра).")]
		public float DistanceAmplitude = 1.85f;
		[Tooltip("Полный цикл «ближе — дальше», секунды.")]
		public float DistancePeriod = 5.2f;

		[Header("Вверх / вниз")]
		public float HeightAmplitude = 0.3f;
		public float HeightPeriod = 7f;

		private void OnEnable() => Apply(0f);

		private void OnValidate() => Apply(0f);

		private void LateUpdate() => Apply(Time.time);

		public void Apply(float time)
		{
			// Положительный yaw в Unity — по часовой стрелке, если смотреть сверху.
			float yaw = StartYaw + OrbitSpeed * time;
			float distance = Distance + DistanceAmplitude * Mathf.Sin(time * 2f * Mathf.PI / Mathf.Max(DistancePeriod, 0.1f));
			float bob = HeightAmplitude * Mathf.Sin(time * 2f * Mathf.PI / Mathf.Max(HeightPeriod, 0.1f));

			transform.position = Center + Quaternion.Euler(Pitch, yaw, 0f) * Vector3.back * distance + Vector3.up * bob;
			transform.LookAt(Center);
		}
	}
}
