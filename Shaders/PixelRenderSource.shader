Shader "EliotByte/PixelRender/Source"
{
	Properties
	{
		[Header(Texture Layers)]
		_Layer0 ("Layer 0 - Grass (UV1.x)", 2D) = "white" {}
		_Layer1 ("Layer 1 - Rock (UV1.y)", 2D) = "white" {}
		_Layer2 ("Layer 2 - Sand (UV1.z)", 2D) = "white" {}
		_Layer3 ("Layer 3 - Dirt (UV1.w)", 2D) = "white" {}
		_LayerTiling ("Layer Tiling (UV units per texture)", Float) = 16.0
		_LayerSharpness ("Layer Blend Sharpness", Range(1, 16)) = 4.0
		_LayerNoise ("Layer Blend Noise", Range(0, 1)) = 1.0
		_LayerSkip ("Texel Skip Scale", Range(0, 2)) = 1.0
		_LayerMaxStep ("Texel Skip Max Step", Range(1, 32)) = 16.0
		[ToggleUI] _Layers ("Texture Layers (off — checker)", Float) = 1.0
		_ColorA ("Checker Color A", Color) = (0.95, 0.95, 0.95, 1)
		_ColorB ("Checker Color B", Color) = (0.08, 0.08, 0.08, 1)
		_Checks ("Checker Cells Per UV (xy)", Vector) = (8, 8, 0, 0)

		[Header(Lighting)]
		[ToggleUI] _Lit ("Enable Lighting", Float) = 1.0
		_AmbientStrength ("Ambient Strength", Range(0, 2)) = 1.0
		_ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.65
		_Wrap ("Light Wrap", Range(0, 1)) = 0.25
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma target 4.5
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
			#pragma multi_compile_fog
			#pragma multi_compile_instancing

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "PixelRenderTiles.hlsl"

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
			CBUFFER_END

			// Глобальные переменные от PixelRenderManager (тайлы и клетка — в PixelRenderTiles.hlsl)
			float _PixelRenderSourcePass;
			float _PixelRenderDebugFaces;   // отладка: каждый треугольник своим оттенком серого (разрезы клеток видно сразу)

			TEXTURE2D(_Layer0);
			TEXTURE2D(_Layer1);
			TEXTURE2D(_Layer2);
			TEXTURE2D(_Layer3);
			SAMPLER(sampler_point_repeat);

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				float2 uv : TEXCOORD0;
				float4 weights : TEXCOORD1;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float3 normalWS : TEXCOORD1;
				float2 uv : TEXCOORD2;
				float4 weights : TEXCOORD3;
			};

			half LayerLuminance(half3 c) { return dot(c, half3(0.299h, 0.587h, 0.114h)); }

			float PackHalf2(float a, float b)
			{
				uint ua = f32tof16(a);
				uint ub = f32tof16(b);
				return asfloat((ua << 16) | (ub & 0xFFFF));
			}

			float2 OctEncode(float3 n)
			{
				n /= (abs(n.x) + abs(n.y) + abs(n.z) + 1e-6);
				float2 oct = n.z >= 0.0 ? n.xy : (1.0 - abs(n.yx)) * (n.xy >= 0.0 ? 1.0 : -1.0);
				return oct;
			}

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

			Varyings Vert(Attributes input)
			{
				Varyings output;
				UNITY_SETUP_INSTANCE_ID(input);
				output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
				output.positionCS = TransformWorldToHClip(output.positionWS);
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				output.uv = input.uv;
				output.weights = input.weights;
				return output;
			}

			struct SurfaceCell
			{
				float3 center;
				float3 edgeU;
				float3 edgeV;
			};

			// Клетка — в сетке тайла, к которому относится точка; центр клетки — луч камеры запекания тайла
			// через центр клетки, пересечённый с плоскостью СВОЕГО треугольника (сборка потом берёт цвет клетки
			// из пикселя, который действительно лежит в центре, — так стыки треугольников не режут клетку).
			SurfaceCell FindSurfaceCell(float3 positionWS, float3 normalWS, int tile)
			{
				SurfaceCell result;
				result.center = positionWS;
				result.edgeU = 0.0;
				result.edgeV = 0.0;

				bool valid;
				float2 cellIndex = TileCellOf(tile, positionWS, valid);
				if (!valid) return result;

				float cell = max(_PixelRenderSurfaceCell, 1.0);
				float2 centerScreen = (cellIndex + 0.5) * cell;
				float3 center, plusU, minusU, plusV, minusV;

				if (TileRayOnPlane(tile, centerScreen, positionWS, normalWS, center))
				{
					result.center = center;
					if (TileRayOnPlane(tile, centerScreen + float2(0.5 * cell, 0.0), positionWS, normalWS, plusU) &&
					    TileRayOnPlane(tile, centerScreen - float2(0.5 * cell, 0.0), positionWS, normalWS, minusU) &&
					    TileRayOnPlane(tile, centerScreen + float2(0.0, 0.5 * cell), positionWS, normalWS, plusV) &&
					    TileRayOnPlane(tile, centerScreen - float2(0.0, 0.5 * cell), positionWS, normalWS, minusV))
					{
						result.edgeU = plusU - minusU;
						result.edgeV = plusV - minusV;
					}
				}
				return result;
			}

			float2 PlaneCoefficients(float3 offset, float3 positionX, float3 positionY)
			{
				float xx = dot(positionX, positionX), xy = dot(positionX, positionY), yy = dot(positionY, positionY);
				float gram = xx * yy - xy * xy;
				if (abs(gram) < 1e-30) return 0.0;
				float ox = dot(positionX, offset), oy = dot(positionY, offset);
				return float2(ox * yy - oy * xy, oy * xx - ox * xy) / gram;
			}

			float4 SurfaceFrag(Varyings input, uint primitive)
			{
				float3 positionX = ddx(input.positionWS);
				float3 positionY = ddy(input.positionWS);
				float2 uvX = ddx(input.uv);
				float2 uvY = ddy(input.uv);
				float3 faceNormal = cross(positionX, positionY);
				faceNormal = dot(faceNormal, faceNormal) > 1e-24 ? normalize(faceNormal) : normalize(input.normalWS);
				faceNormal *= dot(faceNormal, input.normalWS) < 0.0 ? -1.0 : 1.0;

				int tile = TileIndexOf(input.positionWS);
				SurfaceCell cell = FindSurfaceCell(input.positionWS, faceNormal, tile);

				float2 toCenter = PlaneCoefficients(cell.center - input.positionWS, positionX, positionY);
				float2 alongU = PlaneCoefficients(cell.edgeU, positionX, positionY);
				float2 alongV = PlaneCoefficients(cell.edgeV, positionX, positionY);

				float2 centerUv = input.uv + uvX * toCenter.x + uvY * toCenter.y;
				float4 centerWeights = input.weights + ddx(input.weights) * toCenter.x + ddy(input.weights) * toCenter.y;
				float2 texelsPerUv = (_Layer0_TexelSize.z > 0 ? _Layer0_TexelSize.zw : float2(64, 64)) / max(_LayerTiling, 1e-4);
				float2 uvU = uvX * alongU.x + uvY * alongU.y;
				float2 uvV = uvX * alongV.x + uvY * alongV.y;
				float2 cellTexels = max(abs(uvU), abs(uvV)) * texelsPerUv;

				float cellPixels = max(_PixelRenderSurfaceCell, 7.0);
				float2 screenTexels = max(abs(uvX), abs(uvY)) * cellPixels * texelsPerUv;
				if (dot(cellTexels, cellTexels) < 1e-4)
					cellTexels = screenTexels;

				// Каскадный пропуск текселей (LOD-каскады без сглаживания и размытия):
				// Уровень 1 (близко): шаг = 1 (все пиксели текстуры как есть)
				// Уровень 2: шаг = 2 (каждый 2-й пиксель)
				// Уровень 3: шаг = 3 (каждый 3-й пиксель) и т.д.
				float2 skipStep = clamp(floor(cellTexels * max(_LayerSkip, 1.0)), 1.0, max(_LayerMaxStep, 16.0));
				float2 texel = floor(floor(centerUv * texelsPerUv) / skipStep) * skipStep + 0.5;
				half3 albedo;

				if (_Layers > 0.5)
					albedo = LayerAlbedo(texel / texelsPerUv, centerWeights);
				else
				{
					// Шахматка (как в пакете): большинство из 4×4 точек клетки, ничья — по центру.
					float2 centerTexel = centerUv * _Checks.xy;
					float darkSamples = 0.0;

					[unroll]
					for (int sy = 0; sy < 4; sy++)
					[unroll]
					for (int sx = 0; sx < 4; sx++)
					{
						float2 off = (float2(sx, sy) + 0.5) / 4.0 - 0.5;
						float2 sampleCell = floor(centerTexel + uvU * _Checks.xy * off.x + uvV * _Checks.xy * off.y);
						darkSamples += fmod(abs(sampleCell.x + sampleCell.y), 2.0);
					}

					float2 centerCell = floor(centerTexel);
					float coverage = darkSamples / 16.0;
					float dark = abs(coverage - 0.5) > 0.001 ? (coverage > 0.5 ? 1.0 : 0.0) : fmod(abs(centerCell.x + centerCell.y), 2.0);
					albedo = lerp(_ColorA.rgb, _ColorB.rgb, dark);
				}

				half3 color = albedo;

				if (_Lit > 0.5)
				{
					half3 normal = normalize(input.normalWS);
					Light light = GetMainLight(TransformWorldToShadowCoord(cell.center));
					half ndotl = saturate((dot(normal, light.direction) + _Wrap) / (1.0h + _Wrap));
					half shadow = lerp(1.0h, light.shadowAttenuation, _ShadowStrength);
					color = albedo * (light.color * ndotl * shadow + SampleSH(normal) * _AmbientStrength);
				}
				color = MixFog(color, ComputeFogFactorZ0ToFar(max(-TransformWorldToView(cell.center).z, 0.0)));

				if (_PixelRenderDebugFaces > 0.5)
				{
					uint h = primitive * 2654435761u;
					h ^= h >> 15;
					color = (float((h >> 8) & 255u) / 255.0) * float3(1, 1, 1);
				}

				float eyeDepth = max(-TransformWorldToView(input.positionWS).z, 0.0);
				color = saturate(color);
				float2 octNormal = OctEncode(faceNormal);
				return float4(
					PackHalf2(color.r, color.g),
					PackHalf2(color.b, tile + 1.0),
					PackHalf2(octNormal.x, octNormal.y),
					eyeDepth
				);
			}

			float4 Frag(Varyings input, uint primitive : SV_PrimitiveID) : SV_Target
			{
				if (_PixelRenderSourcePass > 0.5)
					return SurfaceFrag(input, primitive);

				half3 color;

				if (_Layers > 0.5)
					color = LayerAlbedo(input.uv, input.weights);
				else
				{
					float2 checkCell = floor(input.uv * _Checks.xy);
					color = lerp(_ColorA.rgb, _ColorB.rgb, fmod(abs(checkCell.x + checkCell.y), 2.0));
				}
				if (_Lit > 0.5)
				{
					float3 positionX = ddx(input.positionWS);
					float3 positionY = ddy(input.positionWS);
					float3 faceNormal = cross(positionX, positionY);
					faceNormal = dot(faceNormal, faceNormal) > 1e-24 ? normalize(faceNormal) : normalize(input.normalWS);
					faceNormal *= dot(faceNormal, input.normalWS) < 0.0 ? -1.0 : 1.0;

					Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
					half ndotl = saturate((dot(faceNormal, light.direction) + _Wrap) / (1.0h + _Wrap));
					half shadow = lerp(1.0h, light.shadowAttenuation, _ShadowStrength);
					color = color * (light.color * ndotl * shadow + SampleSH(faceNormal) * _AmbientStrength);
				}
				color = MixFog(color, ComputeFogFactorZ0ToFar(max(-TransformWorldToView(input.positionWS).z, 0.0)));
				return float4(color, 1.0);
			}
			ENDHLSL
		}

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On
			ZTest LEqual
			ColorMask 0

			HLSLPROGRAM
			#pragma vertex ShadowVert
			#pragma fragment ShadowFrag
			#pragma multi_compile_instancing
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

			float3 _LightDirection;
			struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
			float4 ShadowVert(Attributes input) : SV_POSITION
			{
				UNITY_SETUP_INSTANCE_ID(input);
				float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
				float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
				float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
				#if UNITY_REVERSED_Z
					positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
				#else
					positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
				#endif
				return positionCS;
			}
			half4 ShadowFrag() : SV_Target { return 0; }
			ENDHLSL
		}

		Pass
		{
			Name "DepthOnly"
			Tags { "LightMode" = "DepthOnly" }
			ZWrite On
			ColorMask R

			HLSLPROGRAM
			#pragma vertex DepthVert
			#pragma fragment DepthFrag
			#pragma multi_compile_instancing
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
			float4 DepthVert(Attributes input) : SV_POSITION
			{
				UNITY_SETUP_INSTANCE_ID(input);
				return TransformWorldToHClip(TransformObjectToWorld(input.positionOS.xyz));
			}
			half DepthFrag() : SV_Target { return 0; }
			ENDHLSL
		}
	}
	FallBack Off
}
