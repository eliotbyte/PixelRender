Shader "EliotByte/PixelRender/Resolve"
{
	Properties
	{
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+1" }

		Pass
		{
			Name "Resolve"
			Tags { "LightMode" = "UniversalForward" }

			ZWrite On
			ZTest LEqual
			Cull Off

			// Пиксели, закрытые квадами на рёбрах полигонов (PixelRenderEdges), не трогаем.
			Stencil
			{
				Ref 64
				ReadMask 64
				Comp NotEqual
			}

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma target 4.5

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "../PixelRenderTiles.hlsl"

			// Сборка террейна по клеткам тайлов.
			//
			// Правило: пиксель p закрашивает клетка k поверхности s, если
			//   1) луч пикселя, пересечённый с плоскостью s, попадает в клетку k (в сетке тайла s);
			//   2) центр клетки k не «улетел» за поверхность: в его точке на экране видно что-то не дальше
			//      самого центра (сама клетка или то, что её закрывает). Если там небо или более дальний склон —
			//      клетки в этом месте нет (ступенька внутрь).
			// Кандидаты — поверхности в окрестности ±1 клетки; из подходящих побеждает ближайшая (ступенька наружу
			// у силуэта и у ближнего холма поверх дальнего). Цвет клетки берётся из пикселя, лежащего в её центре,
			// — один на всю клетку, стыки треугольников клетку не режут.

			TEXTURE2D_FLOAT(_PixelRenderSource);
			float4x4 _PixelRenderScreenMatrix;
			float4x4 _PixelRenderScreenInverse;
			float4 _PixelRenderCameraPosition;
			float4 _PixelRenderCameraForward;
			float4 _PixelRenderResolveParams;   // x — допуск глубины (доля), y — радиус поиска (клетки), z — отладка

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

			void UnpackHalf2(float packed, out float a, out float b)
			{
				uint u = asuint(packed);
				a = f16tof32(u >> 16);
				b = f16tof32(u & 0xFFFF);
			}

			float3 OctDecode(float2 oct)
			{
				float3 n = float3(oct.x, oct.y, 1.0 - abs(oct.x) - abs(oct.y));
				float t = saturate(-n.z);
				n.xy += n.xy >= 0.0 ? -t : t;
				return normalize(n);
			}

			float3 ScreenRay(float2 screen)
			{
				float2 ndc = screen * _PixelRenderSourceSize.zw * 2.0 - 1.0;
				float4 nearPoint = mul(_PixelRenderScreenInverse, float4(ndc, -1.0, 1.0));
				float4 midPoint = mul(_PixelRenderScreenInverse, float4(ndc, 0.0, 1.0));
				return normalize(midPoint.xyz / midPoint.w - nearPoint.xyz / nearPoint.w);
			}

			float EyeDepthOf(float3 world)
			{
				return dot(world - _PixelRenderCameraPosition.xyz, _PixelRenderCameraForward.xyz);
			}

			bool ScreenOf(float3 world, out float2 screen)
			{
				float4 clip = mul(_PixelRenderScreenMatrix, float4(world, 1.0));
				float2 ndc = clip.xy / max(abs(clip.w), 1e-6);
				screen = (ndc * 0.5 + 0.5) * _PixelRenderSourceSize.xy;
				return clip.w > 0.0;
			}

			bool OnScreen(float2 screen)
			{
				return all(screen >= 0.0) && all(screen < _PixelRenderSourceSize.xy);
			}

			struct Surface
			{
				bool own;
				int tile;
				float3 world;
				float3 normal;
				float depth;
				float3 color;
				float2 cell;
			};

			Surface Load(float2 screen)
			{
				Surface s;
				int2 coord = clamp(int2(floor(screen)), int2(0, 0), int2(_PixelRenderSourceSize.xy) - 1);
				float4 raw = LOAD_TEXTURE2D(_PixelRenderSource, coord);
				s.own = raw.a > 0.0;
				s.depth = raw.a;
				s.tile = 0;
				s.world = 0.0;
				s.normal = float3(0.0, 1.0, 0.0);
				s.color = 0.0;
				s.cell = 0.0;

				if (!s.own)
					return s;

				float r, g, b, id, octX, octY;
				UnpackHalf2(raw.r, r, g);
				UnpackHalf2(raw.g, b, id);
				UnpackHalf2(raw.b, octX, octY);
				s.color = float3(r, g, b);
				s.tile = max(int(round(id)) - 1, 0);
				s.normal = OctDecode(float2(octX, octY));
				float3 ray = ScreenRay(float2(coord) + 0.5);
				s.world = _PixelRenderCameraPosition.xyz + ray * (s.depth / max(dot(ray, _PixelRenderCameraForward.xyz), 1e-4));
				bool valid;
				s.cell = TileCellOf(s.tile, s.world, valid);
				return s;
			}

			bool RayOnPlane(float3 ray, float3 planePoint, float3 planeNormal, out float3 hit)
			{
				float facing = dot(ray, planeNormal);
				hit = planePoint;

				if (abs(facing) < 1e-5)
					return false;

				float t = dot(planePoint - _PixelRenderCameraPosition.xyz, planeNormal) / facing;
				hit = _PixelRenderCameraPosition.xyz + ray * t;
				return t > 0.0;
			}

			static int _cellColorPath;

			// Цвет клетки — из пикселя, который лежит в её опорной точке (центр клетки; если центр в другой области —
			// одна из четвертей клетки, порядок одинаковый для всех пикселей клетки). Точка ищется лучом камеры
			// запекания, пересечённым с плоскостью треугольника; если попали на соседний треугольник (излом),
			// повторяем с его плоскостью.
			bool CellColorAt(Surface s, float2 cell, float2 offset, out float3 color)
			{
				float3 planePoint = s.world;
				float3 planeNormal = s.normal;
				float region = RegionOf(s.tile);
				bool hit = false;
				color = 0.0;

				[unroll]
				for (int i = 0; i < 3; i++)
				{
					float3 cellPoint;
					float2 screen;

					if (!TileCellPoint(s.tile, cell, offset, planePoint, planeNormal, cellPoint) || !ScreenOf(cellPoint, screen) || !OnScreen(screen))
						return hit;

					Surface f = Load(screen);

					if (!f.own || RegionOf(f.tile) != region)
						return hit;

					bool valid;

					if (all(TileCellOf(s.tile, f.world, valid) == cell))
					{
						color = f.color;

						if (hit)
							return true;   // второй раз подряд в своей клетке — точка найдена

						hit = true;
					}
					else if (hit)
						return true;

					planePoint = f.world;
					planeNormal = f.normal;
				}

				return hit;
			}

			float3 CellColor(Surface s, float2 cell, float3 fallback)
			{
				float3 color;
				_cellColorPath = 1;

				if (CellColorAt(s, cell, float2(0.5, 0.5), color)) return color;
				_cellColorPath = 2;
				if (CellColorAt(s, cell, float2(0.25, 0.25), color)) return color;
				if (CellColorAt(s, cell, float2(0.75, 0.25), color)) return color;
				if (CellColorAt(s, cell, float2(0.25, 0.75), color)) return color;
				if (CellColorAt(s, cell, float2(0.75, 0.75), color)) return color;
				_cellColorPath = 0;
				return fallback;
			}

			// Закрашивает ли пиксель с лучом ray клетка сетки тайла tile на плоскости (planePoint, planeNormal):
			//   1) луч на плоскости попадает в клетку (если задана requiredCell — именно в неё);
			//   2) центр клетки лежит в области этого тайла — клетка на границе областей целиком принадлежит той
			//      области, где её центр (шов идёт ступеньками по целым клеткам, без обрезков);
			//   3) центр клетки не за силуэтом: в его точке на экране видно что-то не дальше самого центра.
			bool CellCandidate(int tile, float3 planePoint, float3 planeNormal, float3 ray, float tolerance, float maxDepth,
				bool checkRegion, bool useRequired, float2 requiredCell, out float2 cell, out float hitDepth)
			{
				cell = 0.0;
				hitDepth = 0.0;
				float3 hit;

				if (!RayOnPlane(ray, planePoint, planeNormal, hit))
					return false;

				hitDepth = EyeDepthOf(hit);

				if (hitDepth >= maxDepth || hitDepth <= 0.0)
					return false;

				bool valid;
				cell = TileCellOf(tile, hit, valid);

				if (!valid || (useRequired && any(cell != requiredCell)))
					return false;

				float3 center;

				if (!TileCellCenter(tile, cell, planePoint, planeNormal, center))
					return false;

				if (checkRegion && RegionOf(TileIndexOf(center)) != RegionOf(tile))
					return false;

				float2 centerScreen;

				if (ScreenOf(center, centerScreen) && OnScreen(centerScreen))
				{
					Surface f = Load(centerScreen);

					if (!f.own)
						return false;

					// Дальше центра — центр «за силуэтом», если только там не та же клетка соседней грани
					// (выпуклый излом: плоскость треугольника, продолженная за ребро, висит перед соседней гранью).
					if (f.depth > EyeDepthOf(center) * (1.0 + tolerance) + 0.05)
					{
						bool sameValid;
						bool sameCell = RegionOf(f.tile) == RegionOf(tile) && all(TileCellOf(tile, f.world, sameValid) == cell);

						if (!sameCell)
							return false;
					}
				}

				return true;
			}

			// Пиксель внутри сплошной поверхности: на расстоянии клетки во все стороны — своя поверхность без скачка глубины.
			bool InsideSurface(float2 pixel, float depth, float cellPixels)
			{
				float2 directions[4] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1) };

				[unroll]
				for (int i = 0; i < 4; i++)
				{
					float2 at = pixel + directions[i] * cellPixels;

					if (!OnScreen(at))
						continue;

					Surface n = Load(at);

					if (!n.own || abs(n.depth - depth) > 0.15 * min(n.depth, depth) + 0.3)
						return false;
				}

				return true;
			}

			float ToDeviceDepth(float eyeDepth)
			{
				return (1.0 / max(eyeDepth, 1e-3) - _ZBufferParams.w) / _ZBufferParams.z;
			}

			half4 Frag(Varyings input, out float depth : SV_Depth) : SV_Target
			{
				float2 pixel = floor(input.uv * _PixelRenderSourceSize.xy) + 0.5;
				float3 ray = ScreenRay(pixel);
				float cellPixels = max(_PixelRenderSurfaceCell, 1.0);
				float tolerance = _PixelRenderResolveParams.x > 0.0 ? _PixelRenderResolveParams.x : 0.03;

				if (_PixelRenderResolveParams.z > 0.5 && _PixelRenderResolveParams.z < 1.5)
				{
					// Отладка: почему собственный пиксель не проходит.
					Surface o = Load(pixel);
					if (!o.own) discard;
					depth = ToDeviceDepth(o.depth);
					float3 h;
					if (!RayOnPlane(ray, o.world, o.normal, h)) return half4(1, 0, 0, 1);
					bool v;
					float2 c = TileCellOf(o.tile, h, v);
					if (!v) return half4(1, 0.5, 0, 1);
					if (any(c != o.cell)) return half4(1, 1, 0, 1);
					if (TileIndexOf(h) != o.tile) return half4(0, 1, 1, 1);
					float3 ce; float2 cs;
					if (!TileCellCenter(o.tile, c, o.world, o.normal, ce)) return half4(1, 0, 1, 1);
					if (!ScreenOf(ce, cs)) return half4(0.5, 0, 1, 1);
					if (!OnScreen(cs)) return half4(0, 0, 0, 1);
					Surface f = Load(cs);
					if (!f.own) return half4(0, 0, 1, 1);
					if (f.depth > EyeDepthOf(ce) * (1.0 + tolerance) + 0.05) return half4(0, 0.5, 1, 1);
					return half4(0, 1, 0, 1);
				}

				bool found = false;
				float bestDepth = 1e30;
				Surface best = (Surface)0;
				float2 bestCell = 0.0;

				// Своя поверхность пикселя в сетке своего тайла. Если её клетка здесь есть — чужая клетка перебивает её,
				// только когда она заметно ближе (ближний холм поверх дальнего), а не на продолжении той же поверхности.
				Surface own = Load(pixel);
				float limit = 1e30;
				float ownRegion = own.own ? RegionOf(own.tile) : -1.0;
				float triedRegion = ownRegion;
				bool sawSky = false;
				Surface farthest = (Surface)0;

				if (own.own)
				{
					float2 cell;
					float hitDepth;

					if (CellCandidate(own.tile, own.world, own.normal, ray, tolerance, 1e30, true, false, 0.0, cell, hitDepth))
					{
						found = true;
						bestDepth = own.depth;
						best = own;
						bestCell = cell;
						limit = own.depth * (1.0 - tolerance);
					}
				}

				// Своя клетка нашлась, а ступеньки наружу (клетка соседа поверх своего пикселя) рисуют квады на рёбрах
				// полигонов — перебор соседей не нужен (самая дорогая часть сборки). Он остаётся для пикселей, у которых
				// своей клетки здесь нет (ступенька внутрь: что показать вместо неё).
				bool searchNeighbours = !found || _PixelRenderResolveParams.w < 0.5;

				// Кандидаты: сетка 5×5 с шагом в полклетки (±1 клетка).
				[loop]
				for (int k = 0; k < (searchNeighbours ? 25 : 0); k++)
				{
					if (k == 12)
						continue;

					int2 offset = int2(uint(k) % 5u, uint(k) / 5u) - 2;
					float2 at = pixel + float2(offset) * (0.5 * cellPixels);

					if (!OnScreen(at))
						continue;

					Surface s = Load(at);

					if (!s.own)
					{
						sawSky = true;
						continue;
					}

					if (!farthest.own || s.depth > farthest.depth)
						farthest = s;

					float2 cell;
					float hitDepth;

					// Рядом другая область: своя поверхность в её сетке (клетка, чей центр по ту сторону шва).
					float region = RegionOf(s.tile);

					if (own.own && !found && region != triedRegion)
					{
						triedRegion = region;

						if (CellCandidate(s.tile, own.world, own.normal, ray, tolerance, 1e30, true, false, 0.0, cell, hitDepth))
						{
							found = true;
							bestDepth = own.depth;
							best = own;
							best.tile = s.tile;
							bestCell = cell;
							limit = own.depth * (1.0 - tolerance);
							continue;
						}
					}

					// Клетка соседней поверхности (ступенька наружу у силуэта / ближнего холма).
					if (CellCandidate(s.tile, s.world, s.normal, ray, tolerance, min(bestDepth, limit), true, true, s.cell, cell, hitDepth))
					{
						found = true;
						bestDepth = hitDepth;
						best = s;
						bestCell = cell;
					}
				}

				// Запас: пиксель между клетками двух областей (центры обеих — у соседа) — своя обрезанная клетка.
				if (!found && own.own)
				{
					float2 cell;
					float hitDepth;

					if (CellCandidate(own.tile, own.world, own.normal, ray, tolerance, 1e30, false, false, 0.0, cell, hitDepth))
					{
						found = true;
						bestDepth = own.depth;
						best = own;
						bestCell = cell;
					}
				}

				// Запас у стыка ближнего и дальнего склона (неба рядом нет): клетка ближнего склона отступила внутрь,
				// а клетка дальнего не нашлась — рисуем дальний склон в его клетке.
				if (!found && own.own && !sawSky && farthest.own && farthest.depth > own.depth * (1.0 + 3.0 * tolerance))
				{
					float3 hit;

					if (RayOnPlane(ray, farthest.world, farthest.normal, hit))
					{
						bool valid;
						found = true;
						bestDepth = EyeDepthOf(hit);
						best = farthest;
						bestCell = TileCellOf(farthest.tile, hit, valid);
					}
				}

				// Последний запас: внутри сплошной поверхности (или там, где неба рядом нет) дыр не бывает —
				// своя клетка без проверок.
				if (!found && own.own && (!sawSky || InsideSurface(pixel, own.depth, cellPixels)))
				{
					bool valid;
					found = true;
					bestDepth = own.depth;
					best = own;
					bestCell = TileCellOf(own.tile, own.world, valid);
				}

				if (!found)
					discard;

				depth = ToDeviceDepth(bestDepth);
				float3 resolved = CellColor(best, bestCell, best.color);

				if (_PixelRenderResolveParams.z > 3.5)
				{
					// Отладка: клетка пикселя не совпадает с клеткой его собственной точки поверхности (в её сетке)
					// внутри сплошной поверхности — «зубец». Красный — зубец, зелёный — норма.
					bool notch = false;

					if (own.own && InsideSurface(pixel, own.depth, cellPixels))
					{
						bool v;
						float2 ownCellInBest = TileCellOf(best.tile, own.world, v);
						notch = RegionOf(best.tile) != RegionOf(TileIndexOf(own.world)) && RegionOf(best.tile) != RegionOf(own.tile) ? false : any(ownCellInBest != bestCell);
					}

					return notch ? half4(1, 0, 0, 1) : half4(0, 1, 0, 1);
				}

				if (_PixelRenderResolveParams.z > 2.5)
				{
					// Отладка: номер клетки цветом (область + клетка) — тест ищет клетки, закрашенные не целиком.
					uint h = asuint(RegionOf(best.tile)) * 73856093u ^ uint(int(bestCell.x) + 100000) * 19349663u ^ uint(int(bestCell.y) + 100000) * 83492791u;
					h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
					return half4(float((h >> 16) & 255u) / 255.0, float((h >> 8) & 255u) / 255.0, float(h & 255u) / 255.0, 1.0);
				}

				if (_PixelRenderResolveParams.z > 1.5)
				{
					if (_cellColorPath == 1) return half4(0, 1, 0, 1);
					if (_cellColorPath == 2) return half4(0, 0, 1, 1);
					if (_cellColorPath == 3) return half4(1, 1, 0, 1);
					return half4(1, 0, 0, 1);
				}

				return half4(resolved, 1.0);
			}
			ENDHLSL
		}
	}

	FallBack Off
}
