// Пиксельный рендер без импостеров, проход 2 («сборка»).
// Квад по прямоугольнику объекта на экране. Каждый пиксель экрана попадает в клетку сетки объекта
// (сетка привязана к центру объекта, клетка — _Grid.z пикселей экрана). Цвет и глубина всей клетки берутся
// из источника в центре клетки — если там этот же объект. Иначе:
//  • если центр клетки закрыт объектом ПЕРЕД нами — свой цвет из соседней клетки или самого пикселя (заплатка);
//  • если рядом есть клетка объекта и включён контур — цвет контура;
//  • иначе пусто.
Shader "EliotByte/PixelRender/Legacy/Resolve"
{
	Properties
	{
		[ToggleUI] _Outline ("1-Pixel Outline", Float) = 0
		_OutlineColor ("Outline Color", Color) = (0.06, 0.06, 0.08, 1)
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

		Pass
		{
			Name "Resolve"
			Tags { "LightMode" = "UniversalForward" }

			ZWrite On
			ZTest LEqual
			Cull Off

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			TEXTURE2D_FLOAT(_PixelSource);
			float4 _PixelSourceSize;   // xy — размер источника в пикселях, zw — 1/размер

			CBUFFER_START(UnityPerMaterial)
				float _Outline;
				half4 _OutlineColor;
			CBUFFER_END

			// Задаёт PixelRenderObject через MaterialPropertyBlock.
			float4 _Rect;   // прямоугольник квада на экране: xy — левый нижний угол, zw — размер (пиксели)
			float4 _Grid;   // xy — якорь сетки (пиксели экрана), z — размер клетки, w — id объекта
			float _Baked;   // 1 — объект запечён на поверхность: внутри клетки — пиксели источника как есть
			float _SurfaceGrid; // 1 — сетка на поверхности (см. ветку в Frag)

			// Сетка на поверхности (глобальные, PixelRenderManager).
			float4x4 _PixelScreenMatrix;        // мир → клип основной камеры
			float4x4 _PixelScreenInverse;       // обратная
			float4x4 _PixelSurfaceBake;         // мир → клип камеры в момент запекания сетки
			float4x4 _PixelSurfaceBakeInverse;
			float _PixelSurfaceCell;            // клетка, пиксели экрана
			float4 _PixelCameraPosition;        // xyz — позиция основной камеры
			float4 _PixelCameraForward;         // xyz — её «вперёд»
			// Маска запекания: какая поверхность была в центре каждой клетки экранной сетки в момент запекания
			// (id; 0 — пусто; −1 — неизвестно: за экраном). Клетка (i, j) запечённой сетки — тексель (i, j) + xy.
			TEXTURE2D_FLOAT(_PixelSurfaceMask);
			float4 _PixelSurfaceMaskRect;       // xy — сдвиг в клетках, zw — размер маски; z = 0 — маски нет

			// Силуэт из запечённого ракурса (задаёт PixelRenderObject): маска клеток сетки момента запекания.
			// Маска: rgb — цвет клетки, a > 0 — клетка занята (a = глубина), a = 0 — пусто, a < 0 — неизвестно
			// (центр был закрыт другим объектом или за экраном) — там действует текущая геометрия.
			float _BakedMask;
			TEXTURE2D_FLOAT(_BakeMask);
			float4 _BakeMaskRect;          // xy — номер клетки маски (0,0) в сетке запекания, zw — размер маски
			float4 _BakeGrid;              // xy — якорь сетки запекания, z — клетка
			float _CenterDepth;            // глубина центра объекта сейчас
			float _BakeCenterDepth;        // глубина центра объекта в момент запекания
			float _SpriteScale;            // глубина сейчас / глубина при запекании

			struct Attributes
			{
				float4 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				output.uv = input.uv;
				return output;
			}

			// rgb, id, глубина источника в пикселе экрана.
			float4 Fetch(float2 pixel, out float id)
			{
				int2 coord = clamp(int2(floor(pixel)), int2(0, 0), int2(_PixelSourceSize.xy) - 1);
				float4 value = LOAD_TEXTURE2D(_PixelSource, coord);
				id = floor(value.b / 4.0 + 1e-4);
				value.b -= id * 4.0;
				value.b -= value.b >= 2.0 ? 2.0 : 0.0; // флаг «тёмная клетка» (для гистерезиса источника)
				return value;
			}

			float ToDeviceDepth(float eyeDepth)
			{
				return (1.0 / max(eyeDepth, 1e-3) - _ZBufferParams.w) / _ZBufferParams.z;
			}

			// Отладка (глобальная _PixelDebugPaths = 1): цвет по ветке сборки, нарисовавшей пиксель.
			float _PixelDebugPaths;

			half4 DebugPath(int path, half4 color)
			{
				if (_PixelDebugPaths < 0.5)
					return color;

				half3 palette[9] = { half3(0, 0, 0), half3(0.6, 0.6, 0.6), half3(1, 0, 0), half3(0, 0.3, 1), half3(1, 1, 0),
					half3(0, 1, 0), half3(1, 0, 1), half3(0, 1, 1), half3(1, 0.5, 0) };
				return path == 1 ? color : half4(palette[path], 1.0);
			}

			// Глубина «заплатки» — пикселя, нарисованного по догадке (центр клетки чужой): почти у дальней плоскости.
			// Такой пиксель виден, только если его не нарисовал по-настоящему никто: проигрывает любой настоящей клетке
			// любого объекта. Раньше догадка шла с глубиной ближней своей точки и выигрывала у соседа там, где поверхности
			// сходятся (пол у основания колонны) — полоски пола внутри колонны. tier 1 — ещё дальше (последний запас).
			float PatchDepth(float tier)
			{
				return ToDeviceDepth(_ProjectionParams.z * (0.97 + 0.01 * tier));
			}

			// Самая дальняя из наших видимых точек клетки (16 точек); 0 — нашего не видно.
			float OwnFarthest(float2 cellCenter, float cellPixels)
			{
				float farthest = 0.0;

				[unroll]
				for (int i = 0; i < 16; i++)
				{
					float2 offset = (float2(i & 3, i >> 2) + 0.5) / 4.0 - 0.5;
					float sampleId;
					float4 sample = Fetch(cellCenter + offset * cellPixels, sampleId);

					if (sampleId == _Grid.w && sample.a > 0.0)
						farthest = max(farthest, sample.a);
				}

				return farthest;
			}

			// Цвет клетки по её собственным видимым пикселям: 16 точек по клетке, наши — голосуют, побеждает самый
			// частый цвет (источник считает цвет каждого пикселя в центре его клетки, так что у своих пикселей он
			// общий). xyz — цвет, w — ближайшая глубина; found = 0, если нашего в клетке не видно.
			float4 OwnCellColor(float2 cellCenter, float cellPixels, out float found)
			{
				float4 samples[16];
				float valid[16];
				float nearest = 1e9;

				[unroll]
				for (int i = 0; i < 16; i++)
				{
					float2 offset = (float2(i & 3, i >> 2) + 0.5) / 4.0 - 0.5;
					float sampleId;
					samples[i] = Fetch(cellCenter + offset * cellPixels, sampleId);
					valid[i] = sampleId == _Grid.w && samples[i].a > 0.0 ? 1.0 : 0.0;
					nearest = valid[i] > 0.5 ? min(nearest, samples[i].a) : nearest;
				}

				float4 best = 0;
				float bestVotes = 0.0;

				[unroll]
				for (int a = 0; a < 16; a++)
				{
					float votes = 0.0;

					[unroll]
					for (int b = 0; b < 16; b++)
					{
						float3 difference = abs(samples[a].rgb - samples[b].rgb);
						votes += valid[b] * (difference.r + difference.g + difference.b < 0.03 ? 1.0 : 0.0);
					}

					votes *= valid[a];

					if (votes > bestVotes)
					{
						bestVotes = votes;
						best = samples[a];
					}
				}

				found = bestVotes > 0.0 ? 1.0 : 0.0;
				return float4(best.rgb, nearest);
			}

			// ---------- Сетка на поверхности ----------

			float3 ScreenRay(float2 screen)
			{
				float2 ndc = screen * _PixelSourceSize.zw * 2.0 - 1.0;
				float4 nearPoint = mul(_PixelScreenInverse, float4(ndc, -1.0, 1.0));
				float4 midPoint = mul(_PixelScreenInverse, float4(ndc, 0.0, 1.0));
				return normalize(midPoint.xyz / midPoint.w - nearPoint.xyz / nearPoint.w);
			}

			float3 WorldAt(float2 screen, float eyeDepth)
			{
				float3 ray = ScreenRay(screen);
				return _PixelCameraPosition.xyz + ray * (eyeDepth / max(dot(ray, _PixelCameraForward.xyz), 1e-4));
			}

			bool OwnPoint(float2 screen, out float3 world)
			{
				float id;
				float4 value = Fetch(screen, id);
				world = 0.0;

				if (id != _Grid.w || value.a <= 0.0)
					return false;

				world = WorldAt(floor(screen) + 0.5, value.a);
				return true;
			}

			// Плоскость поверхности объекта у пикселя (он должен быть наш): точка + нормаль по соседним своим пикселям.
			void OwnPlane(float2 screen, out float3 planeAt, out float3 planeNormal)
			{
				planeNormal = float3(0.0, 1.0, 0.0);
				OwnPoint(screen, planeAt);
				float3 alongX, alongY;
				bool hasX = OwnPoint(screen + float2(2.0, 0.0), alongX);

				if (!hasX)
					hasX = OwnPoint(screen - float2(2.0, 0.0), alongX);

				bool hasY = OwnPoint(screen + float2(0.0, 2.0), alongY);

				if (!hasY)
					hasY = OwnPoint(screen - float2(0.0, 2.0), alongY);

				if (hasX && hasY)
				{
					float3 cross_ = cross(alongX - planeAt, alongY - planeAt);

					if (dot(cross_, cross_) > 1e-14)
						planeNormal = normalize(cross_);
				}
			}

			float2 SurfaceBakeScreen(float3 world, out float w)
			{
				float4 clip = mul(_PixelSurfaceBake, float4(world, 1.0));
				w = clip.w;
				return (clip.xy / max(clip.w, 1e-6) * 0.5 + 0.5) * _PixelSourceSize.xy;
			}

			bool SurfaceBakeRayOnPlane(float2 bakeScreen, float3 planePoint, float3 planeNormal, out float3 hit)
			{
				float2 ndc = bakeScreen * _PixelSourceSize.zw * 2.0 - 1.0;
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

			half4 Frag(Varyings input, out float depth : SV_Depth) : SV_Target
			{
				float2 pixel = _Rect.xy + input.uv * _Rect.zw;

				// Сетка на поверхности (пол, большие объекты): клетки уже целиком посчитаны в источнике (цвет в центре
				// клетки на поверхности), так что свой пиксель — как есть. Где пиксель закрыт объектом ближе, его
				// ступенчатый силуэт может не дотянуть до настоящего края — щель закрываем ближайшим своим пикселем
				// (8 направлений, до 2 клеток) с глубиной заплатки: видно, только если не нарисовал никто.
				// Сетка на поверхности (пол, большие объекты). Цвет клетки источник уже посчитал в её центре, тут решается
				// только, занята ли клетка (край ступеньками, как у объектов): пиксель → точка на плоскости поверхности
				// (своя или ближайшего своего пикселя) → клетка запечённой экранной сетки → её центр на плоскости →
				// текущий экран. Клетка занята, если в центре наша поверхность (или её закрывает объект ближе).
				if (_SurfaceGrid > 0.5)
				{
					float2 sPixel = floor(pixel) + 0.5;
					float sId;
					float4 sValue = Fetch(sPixel, sId);
					bool sOwn = sId == _Grid.w && sValue.a > 0.0;
					bool sOccupied = sId > 0.0 && sValue.a > 0.0;
					float cellPixels = max(_PixelSurfaceCell, 1.0);
					float2 sSource = sPixel;
					bool sFound = sOwn;
					float3 sNeighbourColor = 0;

					// Чужой пиксель — ближайший свой в радиусе двух клеток: от него плоскость и запасной цвет.
					if (!sOwn)
					{
						float2 directions[8] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
							float2(0.7071, 0.7071), float2(-0.7071, 0.7071), float2(0.7071, -0.7071), float2(-0.7071, -0.7071) };

						[unroll]
						for (int surfaceRing = 1; surfaceRing <= 4; surfaceRing++)
						{
							[unroll]
							for (int surfaceDirection = 0; surfaceDirection < 8; surfaceDirection++)
							{
								float2 candidate = floor(sPixel + directions[surfaceDirection] * (surfaceRing * 0.5 * cellPixels)) + 0.5;
								float candidateId;
								float4 candidateValue = Fetch(candidate, candidateId);

								if (!sFound && candidateId == _Grid.w && candidateValue.a > 0.0)
								{
									sFound = true;
									sSource = candidate;
									sNeighbourColor = candidateValue.rgb;
								}
							}
						}
					}

					int sPath = sOwn ? 4 : 0;
					half3 sColor = sValue.rgb;
					float sDepth = ToDeviceDepth(sValue.a);

					if (sFound)
					{
						float3 planePoint, planeNormal;
						OwnPlane(sSource, planePoint, planeNormal);
						float3 cameraPosition = _PixelCameraPosition.xyz;
						float3 ray = ScreenRay(sPixel);
						float3 world = planePoint;
						bool hit = sOwn;

						if (sOwn)
							world = WorldAt(sPixel, sValue.a);
						else
						{
							float facing = dot(ray, planeNormal);

							if (abs(facing) > 1e-5)
							{
								float t = dot(planePoint - cameraPosition, planeNormal) / facing;

								if (t > 0.0)
								{
									world = cameraPosition + ray * t;
									hit = true;
								}
							}
						}

						float bakeW;
						float2 bakeScreen = SurfaceBakeScreen(world, bakeW);
						float2 bakeCell = floor(bakeScreen / cellPixels);
						float2 centerBake = (bakeCell + 0.5) * cellPixels;
						float3 center;

						// Решение из маски запекания: клетка занята / пуста — как при запекании, до следующего запекания
						// (край не дрожит). Неизвестные клетки (за экраном или под другим объектом) — по текущему кадру ниже.
						float maskValue = -1.0;
						int2 maskTexel = int2(bakeCell + _PixelSurfaceMaskRect.xy);

						if (hit && bakeW > 0.0 && _PixelSurfaceMaskRect.z > 0.0 && all(maskTexel >= 0) && all(maskTexel < int2(_PixelSurfaceMaskRect.zw)))
							maskValue = LOAD_TEXTURE2D(_PixelSurfaceMask, maskTexel).r;

						bool maskOwn = maskValue == _Grid.w;
						bool maskEmpty = maskValue == 0.0;

						if (maskOwn || maskEmpty)
						{
							if (sOwn)
							{
								if (maskEmpty)
									sPath = 0;
							}
							else if (maskOwn)
							{
								// Чужой пиксель в занятой клетке: цвет — из центра клетки, если он сейчас виден, иначе от соседа.
								float4 centerClip = mul(_PixelScreenMatrix, float4(world, 1.0));
								float worldDepth = dot(world - cameraPosition, _PixelCameraForward.xyz);
								float3 cellColor = sNeighbourColor;

								if (SurfaceBakeRayOnPlane(centerBake, planePoint, planeNormal, center))
								{
									centerClip = mul(_PixelScreenMatrix, float4(center, 1.0));
									float2 centerScreen = (centerClip.xy / max(centerClip.w, 1e-6) * 0.5 + 0.5) * _PixelSourceSize.xy;
									float centerId;
									float4 centerValue = Fetch(centerScreen, centerId);

									if (centerClip.w > 0.0 && centerId == _Grid.w && centerValue.a > 0.0)
										cellColor = centerValue.rgb;
								}

								sPath = 8;
								sColor = cellColor;
								sDepth = sOccupied && sValue.a < worldDepth ? PatchDepth(0.0) : ToDeviceDepth(worldDepth);
							}
						}
						else if (hit && bakeW > 0.0 && SurfaceBakeRayOnPlane(centerBake, planePoint, planeNormal, center))
						{
							float4 centerClip = mul(_PixelScreenMatrix, float4(center, 1.0));
							float2 centerScreen = (centerClip.xy / max(centerClip.w, 1e-6) * 0.5 + 0.5) * _PixelSourceSize.xy;

							if (centerClip.w > 0.0 && all(centerScreen >= 0.0) && all(centerScreen < _PixelSourceSize.xy))
							{
								float centerId;
								float4 centerValue = Fetch(centerScreen, centerId);
								bool centerOwn = centerId == _Grid.w && centerValue.a > 0.0;
								// Объект в центре клетки не дальше её (+2%) — центр закрыт им. На линии касания (основание куба на полу)
								// глубины почти равны: раньше при пороге −2% клетка считалась «за краем» и пиксель не рисовал никто.
								bool centerHidden = centerId > 0.0 && centerValue.a > 0.0 && !centerOwn && centerValue.a < centerClip.w * 1.02;

								if (sOwn)
								{
									// Свой пиксель, а центр его клетки — за краем поверхности: клетка пустая (ступенька внутрь).
									if (!centerOwn && !centerHidden)
										sPath = 0;
								}
								else if (centerOwn)
								{
									// Чужой пиксель в занятой клетке: ступенька наружу. Если пиксель закрыт объектом ближе —
									// глубина заплатки (не перебьёт сам объект, закроет щель у его ступенчатого края).
									float worldDepth = dot(world - cameraPosition, _PixelCameraForward.xyz);
									sPath = 8;
									sColor = centerValue.rgb;
									sDepth = sOccupied && sValue.a < worldDepth ? PatchDepth(0.0) : ToDeviceDepth(worldDepth);
								}
								else if (centerHidden && sOccupied)
								{
									sPath = 8;
									sColor = sNeighbourColor;
									sDepth = PatchDepth(0.0);
								}
							}
						}
					}

					if (sPath == 0)
						discard;

					depth = sDepth;
					return DebugPath(sPath, half4(sColor, 1.0));
				}

				// Запечённый силуэт: пиксель → точка (поверхности, если это наш пиксель; иначе — на глубине центра
				// объекта) → её клетка в сетке момента запекания → маска. Край меняется только при новом запекании.
				// Запечённый объект: картинка момента запекания (уже пиксельная — клетка один цвет, вместе с краем)
				// переносится на экран как цельный спрайт: сдвиг на ЦЕЛЫЕ пиксели вслед за центром объекта и масштаб по
				// глубине (подлетели — больше, отлетели — меньше). Одно преобразование для всех пикселей — ни бахромы
				// у края, ни полос на стыках граней. Неточность (поворот, перспектива) копится до следующего запекания.
				if (_BakedMask > 0.5)
				{
					float2 pixelCenter = floor(pixel) + 0.5;
					float2 bakeScreen = _BakeGrid.xy + (pixelCenter - round(_Grid.xy)) * _SpriteScale;
					int2 maskCell = int2(floor((bakeScreen - _BakeGrid.xy) / _BakeGrid.z) - _BakeMaskRect.xy);
					float4 mask = float4(0, 0, 0, -1);

					if (all(maskCell >= 0) && all(maskCell < int2(_BakeMaskRect.zw)))
						mask = LOAD_TEXTURE2D(_BakeMask, maskCell);

					// Занятая клетка — её цвет; глубина — запечённая глубина клетки, сдвинутая вместе с объектом
					// (с другими объектами разбирается буфер глубины).
					if (mask.a > 0.0)
					{
						// Глубина клетки — одна на клетку (иначе граница с другими объектами пошла бы по пикселям, а не
						// ступеньками): текущая глубина объекта в центре клетки, если он там сейчас виден; иначе —
						// запечённая, сдвинутая вместе с объектом. (Только запечённая ошибалась при повороте камеры,
						// и клетки соседнего объекта перебивали край — «волосня» по силуэтам.)
						float2 filledCenterBake = _BakeGrid.xy + (floor((bakeScreen - _BakeGrid.xy) / _BakeGrid.z) + 0.5) * _BakeGrid.z;
						float2 filledCenterScreen = round(_Grid.xy) + (filledCenterBake - _BakeGrid.xy) / max(_SpriteScale, 1e-4);
						float filledId;
						float4 filledValue = Fetch(filledCenterScreen, filledId);
						float cellDepth = filledValue.a;

						if (filledId != _Grid.w || filledValue.a <= 0.0)
						{
							// Центр клетки сейчас вне настоящего контура (край спрайта) — глубина ближайшего видимого
							// пикселя объекта в этой клетке; только если таких нет — запечённая, сдвинутая с объектом.
							// (Запечённая на краю ошибалась, и клетки соседнего объекта выигрывали — «зубцы».)
							float depthFound;
							float4 depthCell = OwnCellColor(filledCenterScreen, _BakeGrid.z / max(_SpriteScale, 1e-4), depthFound);
							cellDepth = depthFound > 0.5 ? depthCell.a : max(_CenterDepth + (mask.a - _BakeCenterDepth), 1e-3);
						}

						depth = ToDeviceDepth(cellDepth);
						return DebugPath(1, half4(mask.rgb, 1.0));
					}

					if (mask.a == 0.0)
					{
						// Клетка спрайта пустая (край-лесенка внутри настоящего силуэта), но сам пиксель — наш, а фона
						// (неба) в клетке нет: значит, позади нас кто-то есть, но в источнике он закрыт нами, и рисовать
						// пиксель некому — чёрная дырка у краёв капсул в куче. Рисуем себя как последний запас (глубина ещё
						// дальше заплаток): виден, только если пиксель не нарисовал никто. На фоне неба лесенка остаётся.
						float fillId;
						float4 fillValue = Fetch(pixel, fillId);

						if (fillId == _Grid.w && fillValue.a > 0.0)
						{
							float fillScale = max(_SpriteScale, 1e-4);
							float2 fillCenterBake = _BakeGrid.xy + (floor((bakeScreen - _BakeGrid.xy) / _BakeGrid.z) + 0.5) * _BakeGrid.z;
							float2 fillCenter = round(_Grid.xy) + (fillCenterBake - _BakeGrid.xy) / fillScale;
							float fillCell = _BakeGrid.z / fillScale;
							float background = 0.0;

							[unroll]
							for (int b = 0; b < 16; b++)
							{
								float2 offset = (float2(b & 3, b >> 2) + 0.5) / 4.0 - 0.5;
								float sampleId;
								float4 sample = Fetch(fillCenter + offset * fillCell, sampleId);
								background += sample.a > 0.0 ? 0.0 : 1.0;
							}

							if (background < 0.5)
							{
								float3 fillColor = fillValue.rgb;
								float fillBest = 1e9;

								[unroll]
								for (int n = 0; n < 9; n++)
								{
									int2 offset = int2(n % 3, n / 3) - 1;
									int2 neighbourCell = maskCell + offset;

									if (any(offset != 0) && all(neighbourCell >= 0) && all(neighbourCell < int2(_BakeMaskRect.zw)))
									{
										float4 neighbourMask = LOAD_TEXTURE2D(_BakeMask, neighbourCell);

										if (neighbourMask.a > 0.0 && neighbourMask.a < fillBest)
										{
											fillBest = neighbourMask.a;
											fillColor = neighbourMask.rgb;
										}
									}
								}

								depth = PatchDepth(1.0);
								return DebugPath(6, half4(fillColor, 1.0));
							}
						}

						discard;
						depth = 0;
						return 0;
					}

					// Неизвестная клетка (в момент запекания её закрывал другой объект или она была за экраном):
					// остаётся целой клеткой того же спрайта; цвет — по большинству наших видимых пикселей в ней.
					// Нашего не видно — см. ниже. (Раньше тут включалась сборка по текущей сетке, и на стыках
					// с перекрывающими объектами получалось зерно чужого размера и цвета.)
					float2 cellCenterBake = _BakeGrid.xy + (floor((bakeScreen - _BakeGrid.xy) / _BakeGrid.z) + 0.5) * _BakeGrid.z;
					float spriteScale = max(_SpriteScale, 1e-4);
					float2 cellCenterScreen = round(_Grid.xy) + (cellCenterBake - _BakeGrid.xy) / spriteScale;
					float ownFound;
					float4 ownCell = OwnCellColor(cellCenterScreen, _BakeGrid.z / spriteScale, ownFound);

					if (ownFound > 0.5)
					{
						depth = ToDeviceDepth(ownCell.a);
						return DebugPath(2, half4(ownCell.rgb, 1.0));
					}

					float edgeId;
					float4 edgeValue = Fetch(pixel, edgeId);

					// Ближайшая известная (занятая) клетка маски среди 8 соседей — ближайшая по глубине (второе кольцо
					// пробовал: в сцене с шахматкой давало лишние одиночные пиксели чужого цвета). Её цвет — цвет спрайта рядом (а не пикселя источника: тот посчитан по текущей
					// сетке и часто другого цвета — получалась бы цветная точка вместо чёрной).
					float candidateRing = 9.0;
					float candidateDepth = 1e9;
					float3 candidateColor = 0;

					if (edgeValue.a > 0.0)
					{
						[unroll]
						for (int n = 0; n < 9; n++)
						{
							int2 offset = int2(n % 3, n / 3) - 1;
							float ring = max(abs(offset.x), abs(offset.y));
							int2 neighbourCell = maskCell + offset;

							if (ring > 0.5 && all(neighbourCell >= 0) && all(neighbourCell < int2(_BakeMaskRect.zw)))
							{
								float4 neighbourMask = LOAD_TEXTURE2D(_BakeMask, neighbourCell);
								float neighbourDepth = max(_CenterDepth + (neighbourMask.a - _BakeCenterDepth), 1e-3);

								if (neighbourMask.a > 0.0 && (ring < candidateRing || (ring == candidateRing && neighbourDepth < candidateDepth)))
								{
									candidateRing = ring;
									candidateDepth = neighbourDepth;
									candidateColor = neighbourMask.rgb;
								}
							}
						}
					}

					// 16 точек клетки нашего не нашли, но сам пиксель — наш (тонкая полоска между краем объекта ближе и
					// краем клетки): рисуем его, иначе на шве между объектами остаётся чёрная точка.
					if (edgeId == _Grid.w && edgeValue.a > 0.0)
					{
						depth = PatchDepth(0.0);
						return DebugPath(2, half4(candidateDepth < 1e8 ? candidateColor : edgeValue.rgb, 1.0));
					}

					// В рендере тут объект ближе нас, но его спрайт сюда может не дотягиваться (отстаёт на долю клетки) —
					// тогда пиксель не рисовал бы никто: чёрные точки «шлейфом» по шву. Рисуем цвет ближайшей известной
					// клетки со своей глубиной: где передний объект есть, он перекроет. Только если объект в пикселе
					// действительно БЛИЖЕ нас — пол позади не считается.
					if (edgeId > 0.0 && candidateDepth < 1e8)
					{
						depth = PatchDepth(0.0);
						return DebugPath(3, half4(candidateColor, 1.0));
					}

					discard;
					depth = 0;
					return 0;
				}
				float cellSize = _Grid.z;
				float2 cell = floor((pixel - _Grid.xy) / cellSize);
				float2 center = _Grid.xy + (cell + 0.5) * cellSize;

				// Занята ли клетка — решаем в ТОЧНОМ (дробном) центре клетки, а не в целом пикселе, куда он попал:
				// 4 соседних пикселя источника с билинейными весами. Иначе при сдвиге сетки на доли пикселя
				// край силуэта прыгал туда-сюда на целую клетку. Цвет у всех пикселей клетки одинаковый (источник
				// считает его в центре клетки), так что берём любой свой — с наибольшим весом.
				float2 corner = center - 0.5;
				float2 corner0 = floor(corner);
				float2 fraction = corner - corner0;
				float own = 0.0, bestOwn = -1.0, bestOther = -1.0;
				float4 ownValue = 0, value = 0;
				float id = 0;

				[unroll]
				for (int k = 0; k < 4; k++)
				{
					float2 step = float2(k & 1, k >> 1);
					float2 weights = lerp(1.0 - fraction, fraction, step);
					float weight = weights.x * weights.y;
					float sampleId;
					float4 sample = Fetch(corner0 + step + 0.5, sampleId);

					if (sampleId == _Grid.w && sample.a > 0.0)
					{
						own += weight;

						if (weight > bestOwn)
						{
							bestOwn = weight;
							ownValue = sample;
						}
					}
					else if (weight > bestOther)
					{
						bestOther = weight;
						value = sample;
						id = sampleId;
					}
				}

				// Запечённый объект: клетки уже нарисованы на его поверхности и едут вместе с ней — внутри объекта
				// показываем источник попиксельно, а сетка решает только, где край (силуэт ступеньками).
				float pixelId;
				float4 pixelValue = Fetch(pixel, pixelId);
				bool pixelOwn = _Baked > 0.5 && pixelId == _Grid.w && pixelValue.a > 0.0;

				if (own >= 0.5)
				{
					if (pixelOwn)
					{
						depth = ToDeviceDepth(pixelValue.a);
						return DebugPath(4, half4(pixelValue.rgb, 1.0));
					}

					depth = ToDeviceDepth(ownValue.a);
					return DebugPath(5, half4(ownValue.rgb, 1.0));
				}

				// Центр клетки — не наш. Если там пусто или объект ДАЛЬШЕ нас — это край силуэта: клетка пустая
				// (лесенка). Если там объект БЛИЖЕ (перекрывает центр клетки), берём свой цвет: из соседней клетки
				// своей сетки, а если и их нет — из самого пикселя. Иначе на краях перекрытий были бы дыры.
				if (id > 0.0 && value.a > 0.0)
				{
					if (pixelOwn && value.a < pixelValue.a)
					{
						depth = ToDeviceDepth(pixelValue.a);
						return DebugPath(6, half4(pixelValue.rgb, 1.0));
					}

					// Свой цвет — прежде всего по видимым пикселям своей же клетки (у них общий цвет центра клетки).
					// Раньше брался цвет соседней клетки или самого пикселя — отсюда одиночные пиксели чужого
					// места текстуры на стыках с перекрывающими объектами.
					float cellFound;
					float4 cellColor = OwnCellColor(center, cellSize, cellFound);

					// Сравниваем с САМОЙ ДАЛЬНЕЙ своей точкой клетки: у пола под лежащей капсулой часть точек перед
					// ней, и с ближней проверка проваливалась — дырка. Ошибиться тут не страшно: заплатка в глубине.
					if (cellFound > 0.5 && value.a < OwnFarthest(center, cellSize))
					{
						depth = PatchDepth(0.0);
						return DebugPath(7, half4(cellColor.rgb, 1.0));
					}

					// Своих пикселей в клетке не видно — из ближайшей своей клетки: сначала 8 соседних, потом второе кольцо (пол под краем
					// колонны: её спрайт сюда уже не дотягивается, а в рендере она закрывает центр клетки пола —
					// без второго кольца тут оставались дырки фонового цвета). Последний вариант — сам пиксель.
					float candidateDepth = 1e9;
					float candidateRing = 9.0;
					float3 candidateColor = 0;

					[unroll]
					for (int i = 0; i < 25; i++)
					{
						float2 offset = float2(i % 5, i / 5) - 2.0;
						float ring = max(abs(offset.x), abs(offset.y));

						if (ring < 0.5 || ring > candidateRing)
							continue;

						float neighbourId;
						float4 neighbour = Fetch(center + offset * cellSize, neighbourId);

						if (neighbourId == _Grid.w && neighbour.a > 0.0 && (ring < candidateRing || neighbour.a < candidateDepth))
						{
							candidateRing = ring;
							candidateDepth = neighbour.a;
							candidateColor = neighbour.rgb;
						}
					}

					if (candidateDepth > 1e8)
					{
						float ownId;
						float4 own = Fetch(pixel, ownId);

						if (ownId == _Grid.w && own.a > 0.0)
						{
							candidateDepth = own.a;
							candidateColor = own.rgb;
						}
					}

					if (candidateDepth < 1e8)
					{
						depth = PatchDepth(0.0);
						return DebugPath(8, half4(candidateColor, 1.0));
					}
				}

				if (_Outline > 0.5)
				{
					float nearest = 1e9;
					float2 offsets[4] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1) };

					[unroll]
					for (int i = 0; i < 4; i++)
					{
						float neighbourId;
						float4 neighbour = Fetch(center + offsets[i] * cellSize, neighbourId);

						if (neighbourId == _Grid.w && neighbour.a > 0.0)
							nearest = min(nearest, neighbour.a);
					}

					if (nearest < 1e8)
					{
						depth = ToDeviceDepth(nearest);
						return _OutlineColor;
					}
				}

				discard;
				depth = 0;
				return 0;
			}
			ENDHLSL
		}

		// Захват маски силуэта в момент запекания: для каждой клетки сетки — занята ли она объектом (по 4 пикселям
		// источника с билинейными весами в точном центре клетки, как в сборке), её цвет и глубина.
		Pass
		{
			Name "CaptureMask"
			// Свой тег — чтобы URP не рисовал этот проход сам (без тега он считается обычным и рисуется у каждого квада).
			Tags { "LightMode" = "PixelCapture" }

			Cull Off
			ZWrite Off
			ZTest Always

			HLSLPROGRAM
			#pragma vertex CaptureVert
			#pragma fragment CaptureFrag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			TEXTURE2D_FLOAT(_PixelSource);
			float4 _PixelSourceSize;
			float4 _CaptureGrid;   // xy — якорь, z — клетка, w — id объекта
			float4 _CaptureRect;   // xy — номер клетки маски (0,0), zw — размер маски
			float _CaptureFarDepth; // БЛИЖАЙШАЯ возможная глубина объекта (центр − радиус): дальше неё — «позади нас», клетка свободна
			// Дозапись маски между запеканиями: «неизвестные» клетки, которые сейчас открылись, снимаются из
			// текущего рендера (по тому же спрайтовому переносу, что в сборке) — и дальше тоже стоят на месте.
			float _CaptureUpdate;   // 1 — режим дозаписи
			float4 _CaptureCurrent; // xy — якорь сейчас (округлённый), z — масштаб спрайта, w — глубина при запекании − глубина сейчас
			TEXTURE2D_FLOAT(_BakeMaskPrev);
			float _CaptureLive;       // 1 — живой силуэт: каждый кадр все клетки, с гистерезисом
			float _CaptureHysteresis; // полуширина полосы: занята при доле > 0.5 + h, пуста при < 0.5 − h

			float4 CaptureVert(float4 positionOS : POSITION) : SV_POSITION
			{
				return float4(positionOS.xy, 0.5, 1.0);
			}

			// Клетка с центром center (пиксели экрана) и размером cellPixels: занята (rgb, a = глубина) / пусто (a = 0) /
			// неизвестно (a = −1).
			float4 CaptureCell(float2 center, float cellPixels)
			{
				if (any(center < 0.0) || any(center >= _PixelSourceSize.xy))
					return float4(0, 0, 0, -1);

				float2 corner = center - 0.5;
				float2 corner0 = floor(corner);
				float2 fraction = corner - corner0;
				float own = 0.0, bestOwn = -1.0, bestOther = -1.0, otherId = 0.0, otherDepth = 0.0;
				float4 ownValue = 0;

				[unroll]
				for (int k = 0; k < 4; k++)
				{
					float2 offset = float2(k & 1, k >> 1);
					float2 weights = lerp(1.0 - fraction, fraction, offset);
					float weight = weights.x * weights.y;
					int2 texel = clamp(int2(corner0 + offset), int2(0, 0), int2(_PixelSourceSize.xy) - 1);
					float4 value = LOAD_TEXTURE2D(_PixelSource, texel);
					float id = floor(value.b / 4.0 + 1e-4);
					value.b -= id * 4.0;
					value.b -= value.b >= 2.0 ? 2.0 : 0.0;

					if (id == _CaptureGrid.w && value.a > 0.0)
					{
						own += weight;

						if (weight > bestOwn)
						{
							bestOwn = weight;
							ownValue = value;
						}
					}
					else if (weight > bestOther)
					{
						bestOther = weight;
						otherId = value.a > 0.0 ? id : 0.0;
						otherDepth = value.a;
					}
				}

				// 16 точек по площади клетки: для цвета по большинству и для глубины объекта рядом.
				float4 samples[16];
				float valid[16];
				float nearestOwn = 1e9;

				[unroll]
				for (int i = 0; i < 16; i++)
				{
					float2 offset = (float2(i & 3, i >> 2) + 0.5) / 4.0 - 0.5;
					int2 texel = clamp(int2(floor(center + offset * cellPixels)), int2(0, 0), int2(_PixelSourceSize.xy) - 1);
					float4 value = LOAD_TEXTURE2D(_PixelSource, texel);
					float id = floor(value.b / 4.0 + 1e-4);
					value.b -= id * 4.0;
					value.b -= value.b >= 2.0 ? 2.0 : 0.0;
					samples[i] = value;
					valid[i] = id == _CaptureGrid.w && value.a > 0.0 ? 1.0 : 0.0;
					nearestOwn = valid[i] > 0.5 ? min(nearestOwn, value.a) : nearestOwn;
				}

				if (own >= 0.5)
				{
					// Цвет клетки — по большинству её пикселей (4×4 точки по площади клетки), а не по одному пикселю
					// у центра: иначе однопиксельная полоска почти невидимой грани (верх колонны под скользящим углом)
					// перекрашивала всю клетку — белые точки по краю.
					float4 best = ownValue;
					float bestVotes = 0.0;

					[unroll]
					for (int a = 0; a < 16; a++)
					{
						float votes = 0.0;

						[unroll]
						for (int b = 0; b < 16; b++)
						{
							float3 difference = abs(samples[a].rgb - samples[b].rgb);
							votes += valid[b] * (difference.r + difference.g + difference.b < 0.03 ? 1.0 : 0.0);
						}

						votes *= valid[a];

						if (votes > bestVotes)
						{
							bestVotes = votes;
							best = samples[a];
						}
					}

					return float4(best.rgb, max(best.a, 1e-3));
				}

				// В центре другой объект. «Неизвестно» (может быть, он закрывает наш) — только если он БЛИЖЕ нас:
				// ближе соседних пикселей нашего объекта, а если их нет — ближе самой дальней точки объекта.
				// Объект позади (пол за краем колонны) — клетка просто пустая; иначе такие клетки рисовались
				// по текущей сетке и давали белые точки по краю.
				float limit = nearestOwn < 1e8 ? nearestOwn : _CaptureFarDepth;
				return otherId > 0.0 && otherDepth < limit * 0.99 ? float4(0, 0, 0, -1) : float4(0, 0, 0, 0);
			}

			// Живой силуэт. Клетка спрайта переносится на экран так же, как в сборке; 16 точек по её площади: наши,
			// свободные (фон или объект дальше) и закрытые (объект ближе нас). Доля нашего — среди видимых (наши +
			// свободные). Клетка становится занятой, когда доля уверенно выше половины, пустой — когда уверенно ниже;
			// в полосе между — прошлое решение (гистерезис, привязанный к объекту, а не к экрану). Цвет уже занятой
			// клетки не меняется — узор внутри стоит на месте до следующего запекания.
			float4 LiveCell(float4 previous, float2 bakeCenter)
			{
				float scale = max(_CaptureCurrent.z, 1e-4);
				float2 center = _CaptureCurrent.xy + (bakeCenter - _CaptureGrid.xy) / scale;
				float cellPixels = _CaptureGrid.z / scale;

				if (any(center < 0.0) || any(center >= _PixelSourceSize.xy))
					return previous;

				float4 samples[16];
				float own[16];
				float nearestOwn = 1e9;

				[unroll]
				for (int i = 0; i < 16; i++)
				{
					float2 offset = (float2(i & 3, i >> 2) + 0.5) / 4.0 - 0.5;
					int2 texel = clamp(int2(floor(center + offset * cellPixels)), int2(0, 0), int2(_PixelSourceSize.xy) - 1);
					float4 value = LOAD_TEXTURE2D(_PixelSource, texel);
					float id = floor(value.b / 4.0 + 1e-4);
					value.b -= id * 4.0;
					value.b -= value.b >= 2.0 ? 2.0 : 0.0;
					value.b = value.a > 0.0 ? value.b : 0.0;
					samples[i] = float4(value.rgb, value.a > 0.0 ? value.a : 0.0);
					own[i] = id == _CaptureGrid.w && value.a > 0.0 ? 1.0 : (value.a > 0.0 ? -id : 0.0);
					nearestOwn = own[i] > 0.5 ? min(nearestOwn, value.a) : nearestOwn;
				}

				float limit = nearestOwn < 1e8 ? nearestOwn : _CaptureFarDepth;
				float ownCount = 0.0, freeCount = 0.0;

				[unroll]
				for (int j = 0; j < 16; j++)
				{
					if (own[j] > 0.5)
						ownCount += 1.0;
					else if (own[j] > -0.5 || samples[j].a >= limit * 0.99)
						freeCount += 1.0; // фон или объект дальше нас
				}

				float visible = ownCount + freeCount;

				// Клетка в основном закрыта объектом ближе — что под ним, не видно: прошлое решение.
				if (visible < 8.0)
					return previous;

				float coverage = ownCount / visible;
				bool wasFilled = previous.a > 0.0;
				bool filled = previous.a < 0.0
					? coverage >= 0.5
					: (wasFilled ? coverage > 0.5 - _CaptureHysteresis : coverage > 0.5 + _CaptureHysteresis);

				if (!filled)
					return float4(0, 0, 0, 0);

				if (wasFilled)
					return previous;

				// Клетка только что стала занятой — цвет по большинству наших точек, глубина — ближайшая наша.
				float4 best = 0;
				float bestVotes = 0.0;

				[unroll]
				for (int a = 0; a < 16; a++)
				{
					float votes = 0.0;

					[unroll]
					for (int b = 0; b < 16; b++)
					{
						float3 difference = abs(samples[a].rgb - samples[b].rgb);
						votes += (own[b] > 0.5 ? 1.0 : 0.0) * (difference.r + difference.g + difference.b < 0.03 ? 1.0 : 0.0);
					}

					votes *= own[a] > 0.5 ? 1.0 : 0.0;

					if (votes > bestVotes)
					{
						bestVotes = votes;
						best = samples[a];
					}
				}

				return float4(best.rgb, max(nearestOwn + _CaptureCurrent.w, 1e-3));
			}

			float4 CaptureFrag(float4 positionCS : SV_POSITION) : SV_Target
			{
				int2 coord = int2(positionCS.xy);
				float2 bakeCenter = _CaptureGrid.xy + (_CaptureRect.xy + coord + 0.5) * _CaptureGrid.z;

				if (_CaptureUpdate < 0.5)
					return CaptureCell(bakeCenter, _CaptureGrid.z);

				float4 previous = LOAD_TEXTURE2D(_BakeMaskPrev, coord);

				if (_CaptureLive > 0.5)
					return LiveCell(previous, bakeCenter);

				if (previous.a >= 0.0)
					return previous;

				float scale = max(_CaptureCurrent.z, 1e-4);
				float4 cell = CaptureCell(_CaptureCurrent.xy + (bakeCenter - _CaptureGrid.xy) / scale, _CaptureGrid.z / scale);

				// Глубину храним «как при запекании» — сборка сдвигает её вместе с объектом.
				if (cell.a > 0.0)
					cell.a = max(cell.a + _CaptureCurrent.w, 1e-3);

				return cell;
			}
			ENDHLSL
		}

		// Маска запекания сетки поверхностей (рисует PixelRenderManager в кадр запекания, сразу после источника):
		// в каждой клетке — id поверхности в её центре (сейчас экран = экран запекания), 0 — пусто, −1 — за экраном.
		Pass
		{
			Name "SurfaceCapture"
			Tags { "LightMode" = "PixelSurfaceCapture" }

			Cull Off
			ZWrite Off
			ZTest Always

			HLSLPROGRAM
			#pragma vertex SurfaceCaptureVert
			#pragma fragment SurfaceCaptureFrag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			TEXTURE2D_FLOAT(_PixelSource);
			float4 _PixelSourceSize;
			float4 _PixelSurfaceMaskRect;
			float _PixelSurfaceCell;

			float4 SurfaceCaptureVert(float4 positionOS : POSITION) : SV_POSITION
			{
				return float4(positionOS.xy, 0.5, 1.0);
			}

			float4 SurfaceCaptureFrag(float4 positionCS : SV_POSITION) : SV_Target
			{
				int2 coord = int2(positionCS.xy);
				float2 center = (float2(coord) - _PixelSurfaceMaskRect.xy + 0.5) * max(_PixelSurfaceCell, 1.0);

				if (any(center < 0.0) || any(center >= _PixelSourceSize.xy))
					return float4(-1.0, 0, 0, 0);

				float4 value = LOAD_TEXTURE2D(_PixelSource, int2(center));
				float id = floor(value.b / 4.0 + 1e-4);
				return float4(value.a > 0.0 ? id : 0.0, 0, 0, 0);
			}
			ENDHLSL
		}
	}

	FallBack Off
}
