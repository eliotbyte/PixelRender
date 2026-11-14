#ifndef SANDBOX_SURFACE_TILES_INCLUDED
#define SANDBOX_SURFACE_TILES_INCLUDED

// Тайлы террейна: у каждого тайла своя запечённая сетка (матрица «мир → экран» того кадра, когда тайл
// перепекался). Общая часть для источника и сборки — чтобы клетки считались одинаково.

StructuredBuffer<float4x4> _PixelRenderTileBake;
StructuredBuffer<float4x4> _PixelRenderTileBakeInverse;
StructuredBuffer<float> _PixelRenderTileStamp;   // номер запекания тайла: у тайлов с одним номером одна сетка (одна область)
float4 _PixelRenderTileGrid;     // xy — мировой XZ угла сетки тайлов, z — размер тайла (м), w — 1 / размер
float4 _PixelRenderTileCount;    // xy — тайлов по X и Z
float4 _PixelRenderSourceSize;   // xy — размер источника в пикселях, zw — 1/размер
float _PixelRenderSurfaceCell;   // размер клетки в пикселях

// Канал номера тайла в источнике (half) несёт ещё «уровень наложения» 0..3 (_PixelRenderOverlay материала): знак — бит 0,
// +1024 — бит 1. Поверхность уровня L лежит поверх поверхностей ниже (окно на стене, дорога над террейном, подъезд над
// скатом дороги): квад на ребре не рисуется, если в центре его клетки виден уровень выше его собственного.
// Ограничение: тайлов < 1023 (half точно хранит целые до 2048).
float EncodeTileLevel(int tile, float level)
{
	int l = clamp(int(round(level)), 0, 3);
	float m = tile + 1.0 + ((l & 2) != 0 ? 1024.0 : 0.0);
	return (l & 1) != 0 ? -m : m;
}

void DecodeTileLevel(float v, out int tile, out int level)
{
	float m = abs(v);
	level = (v < 0.0 ? 1 : 0) + (m > 1024.5 ? 2 : 0);
	tile = max(int(round(m > 1024.5 ? m - 1024.0 : m)) - 1, 0);
}

int TileIndexOf(float3 world)
{
	int2 tile = clamp(int2(floor((world.xz - _PixelRenderTileGrid.xy) * _PixelRenderTileGrid.w)), int2(0, 0), int2(_PixelRenderTileCount.xy) - 1);
	return tile.x + tile.y * int(_PixelRenderTileCount.x);
}

float RegionOf(int tile)
{
	return _PixelRenderTileStamp[tile];
}

// Экранные координаты в пикселях источника. Без переворота по Y: текстура источника в Unity хранится
// так, что пиксель (0,0) — левый нижний угол, как у матрицы камеры (так же в пакете PixelRender).
float2 TileBakeScreen(int tile, float3 world, out float w)
{
	float4 clip = mul(_PixelRenderTileBake[tile], float4(world, 1.0));
	w = clip.w;
	float2 ndc = clip.xy / max(abs(clip.w), 1e-6);
	return (ndc * 0.5 + 0.5) * _PixelRenderSourceSize.xy;
}

float2 TileCellOf(int tile, float3 world, out bool valid)
{
	float w;
	float2 screen = TileBakeScreen(tile, world, w);
	valid = w > 0.0;
	return floor(screen / max(_PixelRenderSurfaceCell, 1.0));
}

// Луч камеры запекания через точку экрана запекания → пересечение с плоскостью.
bool TileRayOnPlane(int tile, float2 bakeScreen, float3 planePoint, float3 planeNormal, out float3 hit)
{
	float2 ndc = bakeScreen * _PixelRenderSourceSize.zw * 2.0 - 1.0;
	float4 nearPoint = mul(_PixelRenderTileBakeInverse[tile], float4(ndc, -1.0, 1.0));
	float4 midPoint = mul(_PixelRenderTileBakeInverse[tile], float4(ndc, 0.0, 1.0));
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

// Точка клетки на плоскости (offset — доля клетки, 0.5 — центр).
bool TileCellPoint(int tile, float2 cell, float2 offset, float3 planePoint, float3 planeNormal, out float3 center)
{
	return TileRayOnPlane(tile, (cell + offset) * max(_PixelRenderSurfaceCell, 1.0), planePoint, planeNormal, center);
}

bool TileCellCenter(int tile, float2 cell, float3 planePoint, float3 planeNormal, out float3 center)
{
	return TileCellPoint(tile, cell, 0.5, planePoint, planeNormal, center);
}

#endif
