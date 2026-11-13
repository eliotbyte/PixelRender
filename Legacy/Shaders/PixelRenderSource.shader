// Пиксельный рендер без импостеров, проход 1 («источник»).
// Все пиксельные объекты рисуются обычным образом в полном разрешении — но цвет каждой точки сразу считается
// «как у крупного пикселя» объекта: шахматка — по большинству в пределах клетки, свет — в точном центре клетки
// (одинаково для всех пикселей клетки, поэтому сборке не важно, какой из них она возьмёт).
// Перспектива обычная.
// В источник (камера PixelRenderManager, текстура ARGBFloat) пишется: rgb — цвет (линейный), к b прибавлен id*4,
// a — глубина от камеры. Дальше PixelRenderResolve собирает каждый объект по его сетке.
// Для любой другой камеры (Scene view и т. п.) шейдер рисует обычный цвет.
Shader "EliotByte/PixelRender/Legacy/Source"
{
	Properties
	{
		_ColorA ("Checker Color A", Color) = (0.95, 0.95, 0.95, 1)
		_ColorB ("Checker Color B", Color) = (0.08, 0.08, 0.08, 1)
		_Checks ("Checker Cells Per UV (xy)", Vector) = (8, 8, 0, 0)
		[ToggleUI] _Lit ("Lighting (off = pure colors)", Float) = 0
		[ToggleUI] _TexelLighting ("Lighting & Shadows Per Texel (whole checker cell gets one shade)", Float) = 0
		_AmbientStrength ("Ambient Strength", Range(0, 2)) = 1
		_ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.65
		_Wrap ("Light Wrap", Range(0, 1)) = 0.25

		[Header(Texture Layers)]
		[Toggle(_PIXELRENDER_LAYERS)] _Layers ("Texture Layers Instead Of Checker (Surface Grid)", Float) = 0
		_Layer0 ("Layer 0 (weight UV1.x)", 2D) = "white" {}
		_Layer1 ("Layer 1 (weight UV1.y)", 2D) = "white" {}
		_Layer2 ("Layer 2 (weight UV1.z)", 2D) = "white" {}
		_Layer3 ("Layer 3 (weight UV1.w)", 2D) = "white" {}
		_LayerTiling ("Layer Tiling (UV units per texture)", Float) = 3
		_LayerSharpness ("Layer Blend Sharpness (1 = soft)", Range(1, 16)) = 4
		_LayerNoise ("Layer Blend Noise (by texture brightness)", Range(0, 1)) = 1
		_LayerSkip ("Texel Skip Scale (0 = off, 1 = step = texels per cell)", Range(0, 2)) = 1
		_LayerMaxStep ("Texel Skip Max Step", Range(1, 32)) = 16
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

		HLSLINCLUDE
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

		CBUFFER_START(UnityPerMaterial)
			half4 _ColorA;
			half4 _ColorB;
			float4 _Checks;
			float _Lit;
			float _TexelLighting;
			half _AmbientStrength;
			half _ShadowStrength;
			half _Wrap;
			float4 _Layer0_TexelSize;
			float _LayerTiling;
			float _LayerSkip;
			float _LayerMaxStep;
			half _LayerSharpness;
			half _LayerNoise;
		CBUFFER_END

		// Задаёт PixelRenderObject через MaterialPropertyBlock.
		float _PixelId;            // номер объекта (1, 2, …)
		float _PixelCell;          // размер клетки объекта в пикселях экрана
		float4 _PixelAnchor;       // xy — якорь сетки объекта (пиксели экрана)
		float4x4 _PixelPrevObjectToWorld; // объект → мир в прошлом кадре (для гистерезиса)
		float _PixelSurface;       // 1 — сетка на поверхности (пол и большие объекты), см. FindSurfaceCell
		// Сетка на поверхности (глобальные, PixelRenderManager): экранная сетка камеры в момент запекания.
		float4x4 _PixelSurfaceBake;        // мир → клип камеры в момент запекания
		float4x4 _PixelSurfaceBakeInverse; // обратная
		float _PixelSurfaceCell;           // размер клетки, пиксели экрана

		// Глобальные.
		float _PixelSourcePass;    // 1 — сейчас рисует камера-источник
		float _PixelDebugUnlit;
		float4x4 _PixelScreenMatrix; // мир → клип основной камеры (без переворота под текстуру)
		float4 _PixelSourceSize;      // xy — размер источника в пикселях
		float4x4 _PixelPrevScreenMatrix; // мир → клип основной камеры в прошлом кадре
		float4 _PixelHistoryParams;   // x — история есть, y — полоса гистерезиса, z — допуск по глубине (доля)
		TEXTURE2D_FLOAT(_PixelHistory); // источник прошлого кадра

		// «Запекание» ракурса (задаёт PixelRenderObject): раз в несколько градусов запоминаем сетку объекта
		// (матрица объект → экран того кадра, якорь, клетка). Каждый кадр точка поверхности проецируется в ЭТУ сетку,
		// и цвет её клетки считается в центре клетки — клетки приклеены к объекту и между запеканиями не меняются.
		// Два ракурса — новый и прошлый, между ними растворение по случайному порогу клетки.
		float _PixelBaked;
		float _BakeProgress;
		float4x4 _BakeMatrix;        // объект → клип камеры в момент запекания
		float4x4 _BakeInverse;       // обратная
		float4 _BakeGrid;            // xy — якорь, z — клетка, w — номер запекания
		float4x4 _BakeMatrixOld;
		float4x4 _BakeInverseOld;
		float4 _BakeGridOld;
		ENDHLSL

		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
			#pragma multi_compile_fog
			#pragma shader_feature_local _PIXELRENDER_LAYERS

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

			// Слои текстур (вместо шахматки): до 4 текстур по UV, смешиваются по весам из UV1 (x, y, z, w).
			TEXTURE2D(_Layer0);
			TEXTURE2D(_Layer1);
			TEXTURE2D(_Layer2);
			TEXTURE2D(_Layer3);
			SAMPLER(sampler_point_repeat);

			half LayerLuminance(half3 c) { return dot(c, half3(0.299h, 0.587h, 0.114h)); }

			// Цвет слоёв в точке UV. Веса слегка «прорезаны» яркостью текстур — граница рваная, как будто камни
			// проступают сквозь траву. Нет весов (у меша нет UV1) — только слой 0.
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

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				float2 uv : TEXCOORD0;
				float4 weights : TEXCOORD1;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float3 normalWS : TEXCOORD1;
				float2 uv : TEXCOORD2;
				float3 positionOS : TEXCOORD3;
				float3 normalOS : TEXCOORD4;
				float4 weights : TEXCOORD5;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
				output.positionCS = TransformWorldToHClip(positionWS);
				output.positionWS = positionWS;
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				output.uv = input.uv;
				output.positionOS = input.positionOS.xyz;
				output.normalOS = input.normalOS;
				output.weights = input.weights;
				return output;
			}

			struct BakedCell
			{
				half3 albedo;
				float3 positionOS;
				float3 normalOS;
				float random;
			};

			float BakeHash(float2 value)
			{
				return frac(sin(dot(value, float2(12.9898, 78.233))) * 43758.5453);
			}

			float3 BakeUnproject(float4x4 inverse, float2 ndc, float z)
			{
				float4 unprojected = mul(inverse, float4(ndc, z, 1.0));
				return unprojected.xyz / unprojected.w;
			}

			// Точка поверхности → клетка сетки запечённого ракурса → луч той камеры через центр клетки → точка
			// на плоскости этого треугольника (всё в пространстве объекта) → там UV, нормаль и узор (доля тёмного по
			// отпечатку клетки). От текущего ракурса результат не зависит — поэтому клетка не меняется при движении.
			BakedCell EvaluateBakedCell(float4x4 bakeMatrix, float4x4 bakeInverse, float4 grid,
				float3 positionOS, float3 objectX, float3 objectY, float2 uv, float2 uvX, float2 uvY,
				float4 weights, float4 weightsX, float4 weightsY,
				float3 normalOS, float3 normalX, float3 normalY)
			{
				float4 clip = mul(bakeMatrix, float4(positionOS, 1.0));
				float2 screen = (clip.xy / clip.w * 0.5 + 0.5) * _PixelSourceSize.xy;
				float2 screenX = ddx(screen), screenY = ddy(screen);
				float cellPixels = max(grid.z, 1.0);
				float2 cellIndex = floor((screen - grid.xy) / cellPixels);
				float2 cellCenter = grid.xy + (cellIndex + 0.5) * cellPixels;

				float a = 0.0, b = 0.0;
				float3 planeNormal = cross(objectX, objectY);

				if (dot(planeNormal, planeNormal) > 1e-24)
				{
					planeNormal = normalize(planeNormal);
					float2 ndc = cellCenter / _PixelSourceSize.xy * 2.0 - 1.0;
					float3 rayStart = BakeUnproject(bakeInverse, ndc, -1.0);
					float3 rayDirection = BakeUnproject(bakeInverse, ndc, 1.0) - rayStart;
					float facing = dot(rayDirection, planeNormal);

					if (abs(facing) > 1e-5 * length(rayDirection))
					{
						float3 offset = rayStart + rayDirection * (dot(positionOS - rayStart, planeNormal) / facing) - positionOS;
						// Центр клетки дальше ~2 клеток от точки (скользящий угол в момент запекания) — не доверяем.
						float objectPerPixel = (length(objectX) + length(objectY)) / max(length(screenX) + length(screenY), 1e-6);

						if (length(offset) < 2.0 * cellPixels * objectPerPixel)
						{
							float xx = dot(objectX, objectX), xy = dot(objectX, objectY), yy = dot(objectY, objectY);
							float gram = xx * yy - xy * xy;

							if (abs(gram) > 1e-30)
							{
								float ox = dot(objectX, offset), oy = dot(objectY, offset);
								a = (ox * yy - oy * xy) / gram;
								b = (oy * xx - ox * xy) / gram;
							}
						}
					}
				}

				float2 centerUv = uv + uvX * a + uvY * b;
				float determinant = screenX.x * screenY.y - screenY.x * screenX.y;
				float2 uvPerPixelX = 0.0, uvPerPixelY = 0.0;

				if (abs(determinant) > 1e-8)
				{
					uvPerPixelX = (uvX * screenY.y - uvY * screenX.y) / determinant;
					uvPerPixelY = (uvY * screenX.x - uvX * screenX.y) / determinant;
				}

				BakedCell result;
				#if defined(_PIXELRENDER_LAYERS)
				float2 texelsPerUv = _Layer0_TexelSize.zw / max(_LayerTiling, 1e-4);
				float4 centerWeights = weights + weightsX * a + weightsY * b;
				float2 footprintX = uvPerPixelX * cellPixels;
				float2 footprintY = uvPerPixelY * cellPixels;
				float2 cellTexels = max(abs(footprintX), abs(footprintY)) * texelsPerUv;
				float2 skipStep = clamp(floor(cellTexels * _LayerSkip), 1.0, _LayerMaxStep);
				float2 texel = floor(floor(centerUv * texelsPerUv) / skipStep) * skipStep + 0.5;
				result.albedo = LayerAlbedo(texel / texelsPerUv, centerWeights);
				#else
				float2 centerTexel = centerUv * _Checks.xy;
				float2 footprintX = uvPerPixelX * _Checks.xy * cellPixels;
				float2 footprintY = uvPerPixelY * _Checks.xy * cellPixels;
				float dark = fmod(abs(floor(centerTexel.x) + floor(centerTexel.y)), 2.0);
				float darkSamples = 0.0;

				[unroll]
				for (int sy = 0; sy < 4; sy++)
				{
					[unroll]
					for (int sx = 0; sx < 4; sx++)
					{
						float2 sampleOffset = (float2(sx, sy) + 0.5) / 4.0 - 0.5;
						float2 sampleCell = floor(centerTexel + footprintX * sampleOffset.x + footprintY * sampleOffset.y);
						darkSamples += fmod(abs(sampleCell.x + sampleCell.y), 2.0);
					}
				}

				float coverage = darkSamples / 16.0;
				dark = abs(coverage - 0.5) > 0.001 ? (coverage > 0.5 ? 1.0 : 0.0) : dark;
				result.albedo = lerp(_ColorA.rgb, _ColorB.rgb, dark);
				#endif
				result.positionOS = positionOS + objectX * a + objectY * b;
				result.normalOS = normalOS + normalX * a + normalY * b;
				result.random = BakeHash(cellIndex + grid.w * 17.31);
				return result;
			}

			// Экранная позиция точки (пиксели, как Camera.WorldToScreenPoint) — в тех же координатах,
			// что якорь сетки объекта.
			float2 ScreenOf(float3 positionWS)
			{
				float4 clip = mul(_PixelScreenMatrix, float4(positionWS, 1.0));
				return (clip.xy / clip.w * 0.5 + 0.5) * _PixelSourceSize.xy;
			}

			// Экранная позиция и w клипа (w <= 0 — точка позади камеры).
			float3 ScreenOfW(float3 positionWS)
			{
				float4 clip = mul(_PixelScreenMatrix, float4(positionWS, 1.0));
				return float3((clip.xy / max(clip.w, 1e-6) * 0.5 + 0.5) * _PixelSourceSize.xy, clip.w);
			}

			float EdgeOnScreen(float3 from, float3 to)
			{
				float3 a = ScreenOfW(from), b = ScreenOfW(to);
				return a.z <= 0.0 || b.z <= 0.0 ? 1e9 : length(a.xy - b.xy);
			}

			struct SurfaceCell
			{
				float3 center;   // центр клетки на плоскости грани, мир
				float3 edgeU;    // рёбра клетки на плоскости грани, мир
				float3 edgeV;
			};

			// Точка → экран камеры запекания (пиксели) и w клипа.
			float2 BakeScreenOf(float3 positionWS, out float w)
			{
				float4 clip = mul(_PixelSurfaceBake, float4(positionWS, 1.0));
				w = clip.w;
				return (clip.xy / max(clip.w, 1e-6) * 0.5 + 0.5) * _PixelSourceSize.xy;
			}

			// Луч камеры запекания через точку её экрана → пересечение с плоскостью.
			bool BakeRayOnPlane(float2 bakeScreen, float3 planePoint, float3 planeNormal, out float3 hit)
			{
				float2 ndc = bakeScreen / _PixelSourceSize.xy * 2.0 - 1.0;
				float4 nearPoint = mul(_PixelSurfaceBakeInverse, float4(ndc, -1.0, 1.0));
				float4 midPoint = mul(_PixelSurfaceBakeInverse, float4(ndc, 0.0, 1.0));
				float3 origin = nearPoint.xyz / nearPoint.w;
				float3 direction = midPoint.xyz / midPoint.w - origin;
				float facing = dot(direction, planeNormal);
				hit = planePoint;

				if (abs(facing) < 1e-6 * length(direction))
					return false;

				float t = dot(planePoint - origin, planeNormal) / facing;
				hit = origin + direction * t;
				return t > 0.0;
			}

			// Сетка на поверхности = ЭКРАННАЯ сетка камеры в момент запекания (PixelRenderManager), перенесённая на
			// поверхность: точка → экран камеры запекания → квадратная клетка _PixelSurfaceCell → луч той камеры через
			// центр клетки → плоскость этой грани. В момент запекания клетки — ровные квадраты экрана одного размера
			// (как у наивной пикселизации), между запеканиями приклеены к поверхности и лишь слегка искажаются
			// (запекание обновляется, когда искажение превышает порог). Все поверхности делят одну сетку.
			SurfaceCell FindSurfaceCell(float3 positionWS, float3 normalWS)
			{
				SurfaceCell result;
				result.center = positionWS;
				result.edgeU = 0.0;
				result.edgeV = 0.0;

				float w;
				float2 bakeScreen = BakeScreenOf(positionWS, w);

				if (w <= 0.0)
					return result;

				float cell = max(_PixelSurfaceCell, 1.0);
				float2 centerScreen = (floor(bakeScreen / cell) + 0.5) * cell;
				float3 center, plusU, minusU, plusV, minusV;

				if (BakeRayOnPlane(centerScreen, positionWS, normalWS, center))
				{
					result.center = center;

					if (BakeRayOnPlane(centerScreen + float2(0.5 * cell, 0.0), positionWS, normalWS, plusU)
					    && BakeRayOnPlane(centerScreen - float2(0.5 * cell, 0.0), positionWS, normalWS, minusU)
					    && BakeRayOnPlane(centerScreen + float2(0.0, 0.5 * cell), positionWS, normalWS, plusV)
					    && BakeRayOnPlane(centerScreen - float2(0.0, 0.5 * cell), positionWS, normalWS, minusV))
					{
						result.edgeU = plusU - minusU;
						result.edgeV = plusV - minusV;
					}
				}

				return result;
			}

			// Смещение по плоскости грани (мир) → коэффициенты по производным позиции (для перевода в UV).
			float2 PlaneCoefficients(float3 offset, float3 positionX, float3 positionY)
			{
				float xx = dot(positionX, positionX), xy = dot(positionX, positionY), yy = dot(positionY, positionY);
				float gram = xx * yy - xy * xy;

				if (abs(gram) < 1e-30)
					return 0.0;

				float ox = dot(positionX, offset), oy = dot(positionY, offset);
				return float2(ox * yy - oy * xy, oy * xx - ox * xy) / gram;
			}

			float4 SurfaceFrag(Varyings input)
			{
				float3 positionX = ddx(input.positionWS), positionY = ddy(input.positionWS);
				float2 uvX = ddx(input.uv), uvY = ddy(input.uv);
				float3 faceNormal = cross(positionX, positionY);
				faceNormal = dot(faceNormal, faceNormal) > 1e-24 ? normalize(faceNormal) : normalize(input.normalWS);
				faceNormal *= dot(faceNormal, input.normalWS) < 0.0 ? -1.0 : 1.0;

				SurfaceCell cell = FindSurfaceCell(input.positionWS, faceNormal);

				// Узор — по большинству 4×4 точек клетки (вдали клетка покрывает много текселей — это и «мип», и стабильность).
				float2 toCenter = PlaneCoefficients(cell.center - input.positionWS, positionX, positionY);
				float2 alongU = PlaneCoefficients(cell.edgeU, positionX, positionY);
				float2 alongV = PlaneCoefficients(cell.edgeV, positionX, positionY);
			#if defined(_PIXELRENDER_LAYERS)
				// Слои: один тексель на клетку, без усреднения и сглаживания. Вдали — «LOD» пропуском текселей:
				// клетка покрывает N текселей по оси текстуры — берём каждый N-й (1, 2, 3, …), отдельно по каждой оси
				// (под скользящим углом текстура сжата по одной оси). Тексель — начало своего блока, точно в центре.
				float2 centerUv = input.uv + uvX * toCenter.x + uvY * toCenter.y;
				float4 centerWeights = input.weights + ddx(input.weights) * toCenter.x + ddy(input.weights) * toCenter.y;
				float2 texelsPerUv = _Layer0_TexelSize.zw / max(_LayerTiling, 1e-4);
				float2 uvU = uvX * alongU.x + uvY * alongU.y;
				float2 uvV = uvX * alongV.x + uvY * alongV.y;
				float2 cellTexels = max(abs(uvU), abs(uvV)) * texelsPerUv;
				float2 skipStep = clamp(floor(cellTexels * _LayerSkip), 1.0, _LayerMaxStep);
				float2 texel = floor(floor(centerUv * texelsPerUv) / skipStep) * skipStep + 0.5;
				half3 albedo = LayerAlbedo(texel / texelsPerUv, centerWeights);

				float dark = 0.0;
			#else
				float2 centerTexel = (input.uv + uvX * toCenter.x + uvY * toCenter.y) * _Checks.xy;
				float2 footprintU = (uvX * alongU.x + uvY * alongU.y) * _Checks.xy;
				float2 footprintV = (uvX * alongV.x + uvY * alongV.y) * _Checks.xy;
				float darkSamples = 0.0;

				[unroll]
				for (int sy = 0; sy < 4; sy++)
				{
					[unroll]
					for (int sx = 0; sx < 4; sx++)
					{
						float2 offset = (float2(sx, sy) + 0.5) / 4.0 - 0.5;
						float2 sampleCell = floor(centerTexel + footprintU * offset.x + footprintV * offset.y);
						darkSamples += fmod(abs(sampleCell.x + sampleCell.y), 2.0);
					}
				}

				float2 centerCell = floor(centerTexel);
				float coverage = darkSamples / 16.0;
				float dark = abs(coverage - 0.5) > 0.001 ? (coverage > 0.5 ? 1.0 : 0.0) : fmod(abs(centerCell.x + centerCell.y), 2.0);
				half3 albedo = lerp(_ColorA.rgb, _ColorB.rgb, dark);
			#endif
				half3 color = albedo;

				// Свет, тень и туман — в центре клетки: граница тени тоже идёт ступеньками клеток.
				if (_Lit > 0.5 && _PixelDebugUnlit < 0.5)
				{
					half3 normal = normalize(input.normalWS);
					Light light = GetMainLight(TransformWorldToShadowCoord(cell.center));
					half ndotl = saturate((dot(normal, light.direction) + _Wrap) / (1.0h + _Wrap));
					half shadow = lerp(1.0h, light.shadowAttenuation, _ShadowStrength);
					color = albedo * (light.color * ndotl * shadow + SampleSH(normal) * _AmbientStrength);
					color = MixFog(color, ComputeFogFactorZ0ToFar(max(-TransformWorldToView(cell.center).z, 0.0)));
				}

				float eyeDepth = -TransformWorldToView(input.positionWS).z;
				color = saturate(color);
				return float4(color.r, color.g, color.b + dark * 2.0 + _PixelId * 4.0, eyeDepth);
			}

			float4 Frag(Varyings input) : SV_Target
			{
				if (_PixelSourcePass > 0.5 && _PixelSurface > 0.5)
					return SurfaceFrag(input);

			#if defined(_PIXELRENDER_LAYERS)
				// Не источник (Scene view, обычные камеры): слои как есть, свет в пикселе.
				if (_PixelSourcePass < 0.5)
				{
					half3 layerColor = LayerAlbedo(input.uv, input.weights);

					if (_Lit > 0.5)
					{
						half3 layerNormal = normalize(input.normalWS);
						Light layerLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
						half layerNdotL = saturate((dot(layerNormal, layerLight.direction) + _Wrap) / (1.0h + _Wrap));
						half layerShadow = lerp(1.0h, layerLight.shadowAttenuation, _ShadowStrength);
						layerColor *= layerLight.color * layerNdotL * layerShadow + SampleSH(layerNormal) * _AmbientStrength;
						layerColor = MixFog(layerColor, ComputeFogFactorZ0ToFar(max(-TransformWorldToView(input.positionWS).z, 0.0)));
					}

					return float4(layerColor, 1.0);
				}
			#endif

				// Запечённый ракурс: клетка — из сетки момента запекания, приклеенной к объекту.
				if (_PixelSourcePass > 0.5 && _PixelBaked > 0.5)
				{
					float3 positionOS = input.positionOS;
					float3 objectX = ddx(positionOS), objectY = ddy(positionOS);
					float2 uvX = ddx(input.uv), uvY = ddy(input.uv);
					float4 weightsX = ddx(input.weights), weightsY = ddy(input.weights);
					float3 normalOS = input.normalOS;
					float3 normalX = ddx(normalOS), normalY = ddy(normalOS);

					BakedCell cell = EvaluateBakedCell(_BakeMatrix, _BakeInverse, _BakeGrid, positionOS, objectX, objectY, input.uv, uvX, uvY, input.weights, weightsX, weightsY, normalOS, normalX, normalY);

					if (cell.random >= _BakeProgress)
						cell = EvaluateBakedCell(_BakeMatrixOld, _BakeInverseOld, _BakeGridOld, positionOS, objectX, objectY, input.uv, uvX, uvY, input.weights, weightsX, weightsY, normalOS, normalX, normalY);

					float3 cellPosition = TransformObjectToWorld(cell.positionOS);
					half3 cellNormal = normalize(TransformObjectToWorldNormal(cell.normalOS));
					half3 cellColor = cell.albedo;

					if (_Lit > 0.5 && _PixelDebugUnlit < 0.5)
					{
						Light cellLight = GetMainLight(TransformWorldToShadowCoord(cellPosition));
						half cellNdotL = saturate((dot(cellNormal, cellLight.direction) + _Wrap) / (1.0h + _Wrap));
						half cellShadow = lerp(1.0h, cellLight.shadowAttenuation, _ShadowStrength);
						cellColor = cell.albedo * (cellLight.color * cellNdotL * cellShadow + SampleSH(cellNormal) * _AmbientStrength);
						cellColor = MixFog(cellColor, ComputeFogFactorZ0ToFar(max(-TransformWorldToView(cellPosition).z, 0.0)));
					}

					float pixelDepth = -TransformWorldToView(input.positionWS).z;
					cellColor = saturate(cellColor);
					return float4(cellColor.r, cellColor.g, cellColor.b + _PixelId * 4.0, pixelDepth);
				}

				float2 texel = input.uv * _Checks.xy;
				float3 texelX = float3(ddx(texel), 0);
				float3 texelY = float3(ddy(texel), 0);
				float2 uvX = ddx(input.uv), uvY = ddy(input.uv);
				float4 weightsX = ddx(input.weights), weightsY = ddy(input.weights);
				float3 positionX = ddx(input.positionWS);
				float3 positionY = ddy(input.positionWS);
				float3 normalX = ddx(input.normalWS);
				float3 normalY = ddy(input.normalWS);

				// Всё ниже считается не в самом пикселе, а в ТОЧНОМ центре клетки сетки объекта (дробный якорь + клетка).
				// Раньше цвет клетки зависел от того, какой целый пиксель источника попал в центр: сетка сдвигается
				// на доли пикселя, этот пиксель прыгает туда-сюда по поверхности — и клетки мигали каждый кадр.
				// Теперь любой пиксель клетки даёт один и тот же ответ, и он меняется с движением плавно.
				float cellPixels = 1.0;
				float2 toCellCenter = 0.0;

				if (_PixelSourcePass > 0.5)
				{
					float2 screen = ScreenOf(input.positionWS);
					float2 screenX = ddx(screen);
					float2 screenY = ddy(screen);
					float determinant = screenX.x * screenY.y - screenY.x * screenX.y;

					if (abs(determinant) > 1e-6)
					{
						// Производные по осям экрана (а не по осям растеризации — у текстуры-цели они могут быть перевёрнуты).
						float3 tx = texelX, ty = texelY, px = positionX, py = positionY, nx = normalX, ny = normalY;
						float2 ux = uvX, uy = uvY;
						float4 wx = weightsX, wy = weightsY;
						texelX = (tx * screenY.y - ty * screenX.y) / determinant;
						texelY = (ty * screenX.x - tx * screenY.x) / determinant;
						uvX = (ux * screenY.y - uy * screenX.y) / determinant;
						uvY = (uy * screenX.x - ux * screenX.y) / determinant;
						weightsX = (wx * screenY.y - wy * screenX.y) / determinant;
						weightsY = (wy * screenX.x - wx * screenX.y) / determinant;
						positionX = (px * screenY.y - py * screenX.y) / determinant;
						positionY = (py * screenX.x - px * screenY.x) / determinant;
						normalX = (nx * screenY.y - ny * screenX.y) / determinant;
						normalY = (ny * screenX.x - nx * screenY.x) / determinant;

						cellPixels = max(_PixelCell, 1.0);
						float2 cellIndex = floor((screen - _PixelAnchor.xy) / cellPixels);
						toCellCenter = _PixelAnchor.xy + (cellIndex + 0.5) * cellPixels - screen;

						// Продлеваем грань до центра клетки только у пикселя, ближайшего к центру (не дальше ~1 px):
						// этого хватает, чтобы цвет клетки не зависел от дробного сдвига сетки. Дальние пиксели — свой
						// настоящий цвет: иначе центр клетки за краем грани (стык граней, силуэт) давал цвет точки,
						// которой на этой грани нет, — «пиксели с соседнего места текстуры».
						if (dot(toCellCenter, toCellCenter) > 1.0)
							toCellCenter = 0.0;
					}
				}

				float2 centerTexel = texel + texelX.xy * toCellCenter.x + texelY.xy * toCellCenter.y;
				float3 centerPosition = input.positionWS + positionX * toCellCenter.x + positionY * toCellCenter.y;
				float3 centerNormal = input.normalWS + normalX * toCellCenter.x + normalY * toCellCenter.y;
				float2 cell = floor(centerTexel);

				// Доля тёмного в пределах клетки объекта (cell × cell пикселей экрана): 4×4 точки по её отпечатку на текстуре.
				float2 footprintX = texelX.xy * cellPixels;
				float2 footprintY = texelY.xy * cellPixels;
				float darkSamples = 0.0;

				[unroll]
				for (int sy = 0; sy < 4; sy++)
				{
					[unroll]
					for (int sx = 0; sx < 4; sx++)
					{
						float2 offset = (float2(sx, sy) + 0.5) / 4.0 - 0.5;
						float2 sampleCell = floor(centerTexel + footprintX * offset.x + footprintY * offset.y);
						darkSamples += fmod(abs(sampleCell.x + sampleCell.y), 2.0);
					}
				}

				float coverage = darkSamples / 16.0;
				float dark = abs(coverage - 0.5) > 0.001 ? (coverage > 0.5 ? 1.0 : 0.0) : fmod(abs(cell.x + cell.y), 2.0);

				// Гистерезис (как у импостера в TerrainSandbox): спорная клетка — доля тёмного близка к половине —
				// берёт решение той же точки поверхности из прошлого кадра (точка перепроецируется матрицами прошлого
				// кадра: и объекта, и камеры). Клетка меняет цвет, только когда доля уверенно ушла за порог.
				if (_PixelSourcePass > 0.5 && _PixelHistoryParams.x > 0.5 && abs(coverage - 0.5) < _PixelHistoryParams.y)
				{
					float3 objectPosition = mul(GetWorldToObjectMatrix(), float4(centerPosition, 1.0)).xyz;
					float3 previousWorld = mul(_PixelPrevObjectToWorld, float4(objectPosition, 1.0)).xyz;
					float4 previousClip = mul(_PixelPrevScreenMatrix, float4(previousWorld, 1.0));

					if (previousClip.w > 0.0)
					{
						float2 previousScreen = (previousClip.xy / previousClip.w * 0.5 + 0.5) * _PixelSourceSize.xy;
						int2 coord = int2(floor(previousScreen));

						if (all(coord >= 0) && all(coord < int2(_PixelSourceSize.xy)))
						{
							float4 history = LOAD_TEXTURE2D(_PixelHistory, coord);
							float historyId = floor(history.b / 4.0 + 1e-4);
							float expectedDepth = previousClip.w;

							if (historyId == _PixelId && history.a > 0.0
							    && abs(history.a - expectedDepth) < _PixelHistoryParams.z * expectedDepth + 0.05)
								dark = history.b - historyId * 4.0 >= 2.0 ? 1.0 : 0.0;
						}
					}
				}
				half3 albedo;
				#if defined(_PIXELRENDER_LAYERS)
				float2 centerUv = input.uv + uvX * toCellCenter.x + uvY * toCellCenter.y;
				float4 centerWeights = input.weights + weightsX * toCellCenter.x + weightsY * toCellCenter.y;
				float2 texelsPerUv = _Layer0_TexelSize.zw / max(_LayerTiling, 1e-4);
				float2 footprintUvX = uvX * cellPixels;
				float2 footprintUvY = uvY * cellPixels;
				float2 cellTexels = max(abs(footprintUvX), abs(footprintUvY)) * texelsPerUv;
				float2 skipStep = clamp(floor(cellTexels * _LayerSkip), 1.0, _LayerMaxStep);
				float2 layerTexel = floor(floor(centerUv * texelsPerUv) / skipStep) * skipStep + 0.5;
				albedo = LayerAlbedo(layerTexel / texelsPerUv, centerWeights);
				#else
				albedo = lerp(_ColorA.rgb, _ColorB.rgb, dark);
				#endif

				// Свет и тени — в центре клетки; с Texel Lighting — ещё и в центре текселя (граница тени «прилипает» к шахматке).
				float3 shadePosition = centerPosition;

				if (_TexelLighting > 0.5)
				{
					float determinant = texelX.x * texelY.y - texelY.x * texelX.y;

					if (abs(determinant) > 1e-8)
					{
						float2 toCenter = cell + 0.5 - centerTexel;
						float stepX = (texelY.y * toCenter.x - texelY.x * toCenter.y) / determinant;
						float stepY = (-texelX.y * toCenter.x + texelX.x * toCenter.y) / determinant;
						shadePosition += positionX * stepX + positionY * stepY;
					}
				}

				half3 normal = normalize(centerNormal);
				Light light = GetMainLight(TransformWorldToShadowCoord(shadePosition));
				half ndotl = saturate((dot(normal, light.direction) + _Wrap) / (1.0h + _Wrap));
				half shadow = lerp(1.0h, light.shadowAttenuation, _ShadowStrength);
				half3 color = albedo * (light.color * ndotl * shadow + SampleSH(normal) * _AmbientStrength);

				float eyeDepth = -TransformWorldToView(input.positionWS).z;
				float shadeDepth = -TransformWorldToView(shadePosition).z;
				color = MixFog(color, ComputeFogFactorZ0ToFar(max(shadeDepth, 0.0)));

				if (_PixelDebugUnlit > 0.5 || _Lit < 0.5)
					color = albedo;

				if (_PixelSourcePass < 0.5)
					return float4(color, 1.0);

				// b: цвет (0..1) + 2, если клетка тёмная (решение для гистерезиса в следующем кадре) + id × 4.
				color = saturate(color);
				return float4(color.r, color.g, color.b + dark * 2.0 + _PixelId * 4.0, eyeDepth);
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
			#pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

			float3 _LightDirection;
			float3 _LightPosition;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
			};

			float4 ShadowVert(Attributes input) : SV_POSITION
			{
				float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
				float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

				#if _CASTING_PUNCTUAL_LIGHT_SHADOW
					float3 lightDirection = normalize(_LightPosition - positionWS);
				#else
					float3 lightDirection = _LightDirection;
				#endif

				float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirection));

				#if UNITY_REVERSED_Z
					positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
				#else
					positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
				#endif

				return positionCS;
			}

			half4 ShadowFrag() : SV_Target
			{
				return 0;
			}
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

			float4 DepthVert(float4 positionOS : POSITION) : SV_POSITION
			{
				return TransformObjectToHClip(positionOS.xyz);
			}

			half DepthFrag() : SV_Target
			{
				return 0;
			}
			ENDHLSL
		}
	}

	FallBack Off
}
