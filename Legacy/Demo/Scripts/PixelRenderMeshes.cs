using System.Collections.Generic;
using UnityEngine;

namespace EliotByte.PixelRender.Legacy.Demo
{
	/// <summary>Меши для сцены PixelRender: UV в метрах (клетка шахматки одного размера на всех гранях).</summary>
	public static class PixelRenderMeshes
	{
		/// <summary>Коробка size с центром в нуле, у каждой грани своя UV-развёртка в метрах.</summary>
		public static Mesh Box(Vector3 size, string name)
		{
			var vertices = new List<Vector3>();
			var normals = new List<Vector3>();
			var uvs = new List<Vector2>();
			var triangles = new List<int>();
			Vector3 half = size * 0.5f;

			void Face(Vector3 normal, Vector3 right, Vector3 up, float width, float height)
			{
				int start = vertices.Count;
				Vector3 center = Vector3.Scale(normal, half);

				for (int i = 0; i < 4; i++)
				{
					float x = i == 1 || i == 2 ? 1f : 0f;
					float y = i >= 2 ? 1f : 0f;
					vertices.Add(center + right * ((x - 0.5f) * width) + up * ((y - 0.5f) * height));
					normals.Add(normal);
					uvs.Add(new Vector2(x * width, y * height));
				}

				triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
			}

			Face(Vector3.forward, Vector3.left, Vector3.up, size.x, size.y);
			Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y);
			Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y);
			Face(Vector3.left, Vector3.back, Vector3.up, size.z, size.y);
			Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z);
			Face(Vector3.down, Vector3.right, Vector3.back, size.x, size.z);

			var mesh = new Mesh { name = name };
			mesh.SetVertices(vertices);
			mesh.SetNormals(normals);
			mesh.SetUVs(0, uvs);
			mesh.SetTriangles(triangles, 0);
			mesh.RecalculateBounds();
			return mesh;
		}
	}
}
