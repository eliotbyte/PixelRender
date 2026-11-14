Shader "EliotByte/PixelRender/Edges"
{
	// Квады на клетках, которые режут рёбра полигонов (список клеток — PixelRenderEdgeCells.compute). Квад — целая клетка
	// на плоскости треугольника, в котором лежит её центр; цвет — как у источника в центре клетки. Закрывает разрез
	// по ребру; остальное рисует сборка. Свойства — как у материала террейна (копируются из него).
	Properties
	{
		_Layer0 ("Layer 0", 2D) = "white" {}
		_Layer1 ("Layer 1", 2D) = "white" {}
		_Layer2 ("Layer 2", 2D) = "white" {}
		_Layer3 ("Layer 3", 2D) = "white" {}
		_LayerTiling ("Layer Tiling", Float) = 16.0
		_LayerSharpness ("Layer Blend Sharpness", Range(1, 16)) = 4.0
		_LayerNoise ("Layer Blend Noise", Range(0, 1)) = 1.0
		_LayerSkip ("Texel Skip Scale", Range(0, 2)) = 1.0
		_LayerMaxStep ("Texel Skip Max Step", Range(1, 32)) = 16.0
		[ToggleUI] _Layers ("Texture Layers (off — checker)", Float) = 1.0
		_ColorA ("Checker Color A", Color) = (0.95, 0.95, 0.95, 1)
		_ColorB ("Checker Color B", Color) = (0.08, 0.08, 0.08, 1)
		_Checks ("Checker Cells Per UV (xy)", Vector) = (8, 8, 0, 0)
		[ToggleUI] _Lit ("Enable Lighting", Float) = 1.0
		_AmbientStrength ("Ambient Strength", Range(0, 2)) = 1.0
		_ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.65
		_Wrap ("Light Wrap", Range(0, 1)) = 0.25
		_PixelRenderOverlay ("Overlay Level", Range(0, 3)) = 0
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-1" }

		Pass
		{
			Name "Edges"
			Tags { "LightMode" = "UniversalForward" }
			ZWrite On
			ZTest LEqual
			Cull Off

			// Квады рисуются раньше сборки и помечают свои пиксели: сборка туда не рисует (иначе на почти скользящей
			// поверхности ближняя часть клетки ближе центра квада и сборка срезает квад по z-буферу).
			Stencil
			{
				Ref 64
				WriteMask 64
				Comp Always
				Pass Replace
			}

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma target 4.5
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile_fog

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "../PixelRenderTiles.hlsl"

			CBUFFER_START(UnityPerMaterial)
				float _LayerTiling;
				float _LayerSharpness;
				float _LayerNoise;
				float _LayerSkip;
				float _LayerMaxStep;
				float _Lit;
				float _AmbientStrength;
				float _ShadowStrength;
				float _Wrap;
				float4 _Layer0_TexelSize;
				float _Layers;
				half4 _ColorA;
				half4 _ColorB;
				float4 _Checks;
				float _EdgeObject;   // номер объекта, чьи квады рисует этот материал
				float _PixelRenderOverlay;   // уровень наложения объекта (копируется из его материала)
			CBUFFER_END

			TEXTURE2D(_Layer0);
			TEXTURE2D(_Layer1);
			TEXTURE2D(_Layer2);
			TEXTURE2D(_Layer3);
			SAMPLER(sampler_point_repeat);

			struct EdgeCell
			{
				float3 center;
				float3 normal;
				float2 cell;
				float2 uv;
				float4 weights;
				float tile;
				float triangleIndex;
				float2 uvU;
				float2 uvV;
				float objectIndex;
				float3 padding;
			};

			StructuredBuffer<EdgeCell> _PixelRenderEdgeCells;
			ByteAddressBuffer _PixelRenderEdgeCount;
			TEXTURE2D_FLOAT(_PixelRenderSource);
			float _PixelRenderDebugFaces;
			float4 _PixelRenderResolveParams;   // z — отладка (3 — номера клеток, 5 — откуда цвет: квад — голубой)
			float4 _PixelRenderCameraPosition;   // игровая камера (квады рисует только она)
			float4 _PixelRenderEdgeParams;      // x — запас квада (доля клетки), y — сдвиг к камере (доля расстояния), z — вместимость

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				nointerpolation float4 color : COLOR;
				float4 screen : TEXCOORD0;            // клип для пикселя источника
				nointerpolation float nearest : TEXCOORD1;   // ближайшая глубина клетки (углы на плоскости треугольника)
			};

			half LayerLuminance(half3 c) { return dot(c, half3(0.299h, 0.587h, 0.114h)); }


			half3 LayerAlbedo(float2 uv, float4 weights)
			{
				uv /= max(_LayerTiling, 1e-4);
				half3 c0 = SAMPLE_TEXTURE2D_LOD(_Layer0, sampler_point_repeat, uv, 0).rgb;
				half3 c1 = SAMPLE_TEXTURE2D_LOD(_Layer1, sampler_point_repeat, uv, 0).rgb;
				half3 c2 = SAMPLE_TEXTURE2D_LOD(_Layer2, sampler_point_repeat, uv, 0).rgb;
				half3 c3 = SAMPLE_TEXTURE2D_LOD(_Layer3, sampler_point_repeat, uv, 0).rgb;

				float4 w = saturate(weights);
				w = dot(w, float4(1, 1, 1, 1)) < 1e-3 ? float4(1, 0, 0, 0) : w;
				float4 lum = float4(LayerLuminance(c0), LayerLuminance(c1), LayerLuminance(c2), LayerLuminance(c3));
				w *= lerp(1.0, saturate(lum * 2.0), _LayerNoise);
				w = pow(max(w, 1e-5), _LayerSharpness);
				w /= max(dot(w, float4(1, 1, 1, 1)), 1e-5);
				return c0 * w.x + c1 * w.y + c2 * w.z + c3 * w.w;
			}

			// Точка клетки (offset — доля клетки) на плоскости; луч почти вдоль плоскости — глубину ограничиваем.
			float3 CellPoint(int tile, float2 cell, float2 offset, float3 center, float3 normal)
			{
				float2 ndc = (cell + offset) * max(_PixelRenderSurfaceCell, 1.0) * _PixelRenderSourceSize.zw * 2.0 - 1.0;
				float4 nearPoint = mul(_PixelRenderTileBakeInverse[tile], float4(ndc, -1.0, 1.0));
				float4 farPoint = mul(_PixelRenderTileBakeInverse[tile], float4(ndc, 1.0, 1.0));
				float3 origin = nearPoint.xyz / nearPoint.w;
				float3 direction = normalize(farPoint.xyz / farPoint.w - origin);
				float centerT = dot(center - origin, direction);
				float facing = dot(direction, normal);
				float t = abs(facing) > 1e-6 ? dot(center - origin, normal) / facing : -1.0;
				t = t > 0.0 ? clamp(t, centerT * 0.8, centerT * 1.25) : centerT;
				return origin + direction * t;
			}

			// Точная точка клетки на плоскости (как в источнике, для шага пропуска текселей).
			float3 PlanePoint(int tile, float2 cell, float2 offset, float3 center, float3 normal)
			{
				float3 hit;
				return TileRayOnPlane(tile, (cell + offset) * max(_PixelRenderSurfaceCell, 1.0), center, normal, hit) ? hit : center;
			}

			Varyings Vert(uint id : SV_VertexID)
			{
				Varyings o;
				o.positionCS = float4(0.0, 0.0, -2.0, 1.0);
				o.color = 0.0;
				o.screen = 0.0;
				o.nearest = 0.0;
				uint quad = id >> 2, corner = id & 3u;

				if (quad >= min(_PixelRenderEdgeCount.Load(0), uint(_PixelRenderEdgeParams.z)))
					return o;

				EdgeCell e = _PixelRenderEdgeCells[quad];
				int tile = int(e.tile);

				if (abs(e.objectIndex - _EdgeObject) > 0.5)
					return o;
				float3 camera = _WorldSpaceCameraPos;

				// Только игровая камера: проверки ниже сделаны по её источнику (в Scene View — мусор).
				if (distance(camera, _PixelRenderCameraPosition.xyz) > 1e-3)
					return o;

				// Только видимые клетки: треугольник смотрит на камеру, центр клетки не закрыт (в источнике в его точке —
				// не ближе самого центра).
				if (dot(e.normal, camera - e.center) <= 0.0)
					return o;

				float4 centerClip = TransformWorldToHClip(e.center);
				float centerDepth = -TransformWorldToView(e.center).z;

				if (centerClip.w <= 0.0)
					return o;

				float2 centerUv = centerClip.xy / centerClip.w * 0.5 + 0.5;
				#if UNITY_UV_STARTS_AT_TOP
				if (_ProjectionParams.x < 0.0) centerUv.y = 1.0 - centerUv.y;
				#endif
				int2 centerPixel = int2(centerUv * _PixelRenderSourceSize.xy);

				if (all(centerPixel >= 0) && all(centerPixel < int2(_PixelRenderSourceSize.xy)))
				{
					float4 centerSource = LOAD_TEXTURE2D(_PixelRenderSource, centerPixel);
					float sourceDepth = centerSource.a;

					// В точке центра видна именно эта поверхность (не ближе — закрыта, не дальше — центр за краем).
					if (sourceDepth <= 0.0 || abs(sourceDepth - centerDepth) > centerDepth * 0.01 + 0.03)
						return o;

					// …и там не поверхность уровня наложения выше своего (окно на стене, дорога над террейном, подъезд над
					// скатом; см. EncodeTileLevel): по глубине их не отличить, квад нижней поверхности рисовался поверх.
					int centerTile, centerLevel;
					DecodeTileLevel(f16tof32(asuint(centerSource.g) & 0xFFFFu), centerTile, centerLevel);
					if (centerLevel > int(round(_PixelRenderOverlay)))
						return o;
				}

				// Угол квада — на луче камеры запекания через угол клетки, на глубине центра клетки (квад параллелен экрану):
				// на вогнутом изломе квад на плоскости треугольника уходил бы под соседнюю грань и срезался по диагонали.
				// Чуть ближе к камере, чем центр — ложится поверх своей клетки; настоящий ближний склон его закрывает.
				// Все 4 угла считаются в каждой вершине: если хоть один уходит дальше пары клеток от центра на экране
				// (луч запекания почти поперёк взгляда — точка улетает) или за камеру — квад отбрасывается целиком.
				float pad = _PixelRenderEdgeParams.x;
				float3 forward = -UNITY_MATRIX_V[2].xyz;
				float targetDepth = centerDepth * (1.0 - _PixelRenderEdgeParams.y);
				float2 centerScreen = centerClip.xy / centerClip.w * 0.5 * _PixelRenderSourceSize.xy;
				float limit = 3.0 * max(_PixelRenderSurfaceCell, 1.0);
				float4 cornerClip[4];

				[unroll]
				for (uint k = 0; k < 4; k++)
				{
					float2 offset = float2(k & 1u, k >> 1) * (1.0 + 2.0 * pad) - pad;
					float2 ndc = (e.cell + offset) * max(_PixelRenderSurfaceCell, 1.0) * _PixelRenderSourceSize.zw * 2.0 - 1.0;
					float4 nearPoint = mul(_PixelRenderTileBakeInverse[tile], float4(ndc, -1.0, 1.0));
					float4 farPoint = mul(_PixelRenderTileBakeInverse[tile], float4(ndc, 1.0, 1.0));
					float3 rayOrigin = nearPoint.xyz / nearPoint.w;
					float3 rayDirection = normalize(farPoint.xyz / farPoint.w - rayOrigin);
					float along = dot(rayDirection, forward);

					if (along <= 0.05)
						return o;

					float t = (targetDepth - dot(rayOrigin - camera, forward)) / along;

					if (t <= 0.0)
						return o;

					cornerClip[k] = TransformWorldToHClip(rayOrigin + rayDirection * t);

					if (cornerClip[k].w <= 0.0 || length(cornerClip[k].xy / cornerClip[k].w * 0.5 * _PixelRenderSourceSize.xy - centerScreen) > limit)
						return o;
				}

				o.positionCS = cornerClip[corner];
				o.screen = o.positionCS;

				// Ближайшая точка клетки на поверхности: углы клетки на плоскости треугольника.
				float nearest = centerDepth;

				[unroll]
				for (uint c = 0; c < 4; c++)
				{
					float3 q = CellPoint(tile, e.cell, float2(c & 1u, c >> 1), e.center, e.normal);
					nearest = min(nearest, -TransformWorldToView(q).z);
				}

				o.nearest = nearest;

				// Цвет — как у источника в центре клетки: текстура с пропуском текселей по размеру клетки (или шахматка —
				// большинство из 4×4 точек клетки), свет, туман. Шаг UV поперёк клетки — из треугольника (uvU, uvV).
				half3 color;

				if (_Layers > 0.5)
				{
					float2 texelsPerUv = (_Layer0_TexelSize.z > 0 ? _Layer0_TexelSize.zw : float2(64, 64)) / max(_LayerTiling, 1e-4);
					float2 cellTexels = max(abs(e.uvU), abs(e.uvV)) * texelsPerUv;
					float2 skipStep = clamp(floor(cellTexels * max(_LayerSkip, 1.0)), 1.0, max(_LayerMaxStep, 16.0));
					float2 texel = floor(floor(e.uv * texelsPerUv) / skipStep) * skipStep + 0.5;
					color = LayerAlbedo(texel / texelsPerUv, e.weights);
				}
				else
				{
					float2 centerTexel = e.uv * _Checks.xy;
					float darkSamples = 0.0;

					[unroll]
					for (int sy = 0; sy < 4; sy++)
					[unroll]
					for (int sx = 0; sx < 4; sx++)
					{
						float2 off = (float2(sx, sy) + 0.5) / 4.0 - 0.5;
						float2 sampleCell = floor(centerTexel + e.uvU * _Checks.xy * off.x + e.uvV * _Checks.xy * off.y);
						darkSamples += fmod(abs(sampleCell.x + sampleCell.y), 2.0);
					}

					float2 centerCell = floor(centerTexel);
					float coverage = darkSamples / 16.0;
					float dark = abs(coverage - 0.5) > 0.001 ? (coverage > 0.5 ? 1.0 : 0.0) : fmod(abs(centerCell.x + centerCell.y), 2.0);
					color = lerp(_ColorA.rgb, _ColorB.rgb, dark);
				}

				if (_Lit > 0.5)
				{
					Light light = GetMainLight(TransformWorldToShadowCoord(e.center));
					half ndotl = saturate((dot(e.normal, light.direction) + _Wrap) / (1.0h + _Wrap));
					half shadow = lerp(1.0h, light.shadowAttenuation, _ShadowStrength);
					color = color * (light.color * ndotl * shadow + SampleSH(e.normal) * _AmbientStrength);
				}

				color = MixFog(color, ComputeFogFactorZ0ToFar(max(-TransformWorldToView(e.center).z, 0.0)));

				if (_PixelRenderDebugFaces > 0.5)
				{
					uint h = uint(e.triangleIndex) * 2654435761u;
					h ^= h >> 15;
					color = (float((h >> 8) & 255u) / 255.0) * float3(1, 1, 1);
				}

				o.color = float4(saturate(color), 1.0);

				if (_PixelRenderResolveParams.z > 4.5 || (_PixelRenderResolveParams.z > 1.5 && _PixelRenderResolveParams.z < 2.5))
					o.color = float4(1, 0, 1, 1);
				else if (_PixelRenderResolveParams.z > 2.5)
				{
					uint h = asuint(RegionOf(tile)) * 73856093u ^ uint(int(e.cell.x) + 100000) * 19349663u ^ uint(int(e.cell.y) + 100000) * 83492791u;
					h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
					o.color = float4(float((h >> 16) & 255u) / 255.0, float((h >> 8) & 255u) / 255.0, float(h & 255u) / 255.0, 1.0);
				}

				return o;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				// Настоящий ближний склон (в источнике поверхность заметно ближе самой ближней точки клетки) — не закрываем.
				float2 uv = input.screen.xy / input.screen.w * 0.5 + 0.5;
				#if UNITY_UV_STARTS_AT_TOP
				if (_ProjectionParams.x < 0.0) uv.y = 1.0 - uv.y;
				#endif
				int2 pixel = clamp(int2(uv * _PixelRenderSourceSize.xy), int2(0, 0), int2(_PixelRenderSourceSize.xy) - 1);
				float sourceDepth = LOAD_TEXTURE2D(_PixelRenderSource, pixel).a;

				// Уступаем только заметно более близкой поверхности (другой склон впереди); соседняя грань того же склона на
				// выпуклом изломе тоже чуть ближе — ей не уступаем, иначе по излому проступает разрез.
				if (sourceDepth > 0.0 && sourceDepth < input.nearest * 0.88 - 0.3)
					discard;

				return half4(input.color);
			}
			ENDHLSL
		}
	}

	FallBack Off
}
