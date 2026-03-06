// --- Game/Editor/EditorRaycast.cs ---
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public static class EditorRaycast
{
    /// <summary>
    /// Строит луч из экранных координат через орбитальную камеру.
    /// </summary>
    public static (Vector3 origin, Vector3 direction) ScreenToRay(
        Vector2 mousePos, int screenWidth, int screenHeight, OrbitalCamera camera)
    {
        float ndcX =  (2.0f * mousePos.X / screenWidth)  - 1.0f;
        float ndcY = -(2.0f * mousePos.Y / screenHeight) + 1.0f;

        Matrix4 invProj = Matrix4.Invert(camera.GetProjectionMatrix());
        Matrix4 invView = Matrix4.Invert(camera.GetViewMatrix());

        // В OpenTK вектор умножается СЛЕВА на матрицу
        Vector4 rayClip = new Vector4(ndcX, ndcY, -1.0f, 1.0f);
        Vector4 rayEye  = rayClip * invProj;  // ← было: invProj * rayClip
        rayEye = new Vector4(rayEye.X, rayEye.Y, -1.0f, 0.0f);

        Vector4 rayWorld = rayEye * invView;  // ← было: invView * rayEye
        Vector3 direction = Vector3.Normalize(new Vector3(rayWorld.X, rayWorld.Y, rayWorld.Z));

        return (camera.Position, direction);
    }

    /// <summary>
    /// Пересекает луч с AABB. Возвращает (tMin, tMax), tMin < 0 если промах.
    /// </summary>
    public static (float tMin, float tMax) IntersectAABB(
        Vector3 origin, Vector3 dir, Vector3 boxMin, Vector3 boxMax)
    {
        Vector3 invDir = new Vector3(
            MathF.Abs(dir.X) > 1e-8f ? 1.0f / dir.X : float.MaxValue,
            MathF.Abs(dir.Y) > 1e-8f ? 1.0f / dir.Y : float.MaxValue,
            MathF.Abs(dir.Z) > 1e-8f ? 1.0f / dir.Z : float.MaxValue);

        Vector3 t0 = (boxMin - origin) * invDir;
        Vector3 t1 = (boxMax - origin) * invDir;

        Vector3 tSmall = Vector3.ComponentMin(t0, t1);
        Vector3 tBig   = Vector3.ComponentMax(t0, t1);

        float tMin = MathF.Max(MathF.Max(tSmall.X, tSmall.Y), tSmall.Z);
        float tMax = MathF.Min(MathF.Min(tBig.X,   tBig.Y),   tBig.Z);

        return (tMin, tMax);
    }

    /// <summary>
    /// Находит воксель под курсором в модели.
    /// Возвращает (localPos, hitNormal, hit).
    /// localPos — координата вокселя в сетке модели.
    /// hitNormal — нормаль грани по которой попали (для Add — куда ставить новый воксель).
    /// </summary>
    public static bool RaycastModel(
        Vector3 rayOrigin, Vector3 rayDir,
        VoxelObject model,
        out Vector3i hitVoxel, out Vector3i hitNormal)
    {
        hitVoxel  = Vector3i.Zero;
        hitNormal = Vector3i.Zero;

        if (model == null || model.VoxelCoordinates.Count == 0)
            return false;

        // Трансформируем луч в локальное пространство модели (как шейдер)
        Matrix4 modelMatrix = model.GetInterpolatedModelMatrix(1.0f);
        Matrix4 invModel    = Matrix4.Invert(modelMatrix);
        rayOrigin = (invModel * new Vector4(rayOrigin, 1.0f)).Xyz;
        rayDir    = Vector3.Normalize((invModel * new Vector4(rayDir, 0.0f)).Xyz);

        // Расширяем AABB немного чтобы луч точно входил
        Vector3 boundsMin = model.LocalBoundsMin - new Vector3(0.01f);
        Vector3 boundsMax = model.LocalBoundsMax + new Vector3(0.01f);

        var (tMin, tMax) = IntersectAABB(rayOrigin, rayDir, boundsMin, boundsMax);
        
        if (tMin > tMax || tMax < 0) return false;

        // DDA по воксельной сетке
        float voxelSize = Constants.VoxelSize;
        float tStart    = MathF.Max(0.0f, tMin) + 0.001f;

        Vector3 pos = rayOrigin + rayDir * tStart;

        // Начальный воксель
        int ix = (int)MathF.Floor(pos.X / voxelSize);
        int iy = (int)MathF.Floor(pos.Y / voxelSize);
        int iz = (int)MathF.Floor(pos.Z / voxelSize);

        int stepX = rayDir.X >= 0 ? 1 : -1;
        int stepY = rayDir.Y >= 0 ? 1 : -1;
        int stepZ = rayDir.Z >= 0 ? 1 : -1;

        float invDx = MathF.Abs(rayDir.X) > 1e-8f ? 1.0f / MathF.Abs(rayDir.X) : float.MaxValue;
        float invDy = MathF.Abs(rayDir.Y) > 1e-8f ? 1.0f / MathF.Abs(rayDir.Y) : float.MaxValue;
        float invDz = MathF.Abs(rayDir.Z) > 1e-8f ? 1.0f / MathF.Abs(rayDir.Z) : float.MaxValue;

        float tDeltaX = voxelSize * invDx;
        float tDeltaY = voxelSize * invDy;
        float tDeltaZ = voxelSize * invDz;

        float boundX = (stepX > 0 ? (ix + 1) : ix) * voxelSize;
        float boundY = (stepY > 0 ? (iy + 1) : iy) * voxelSize;
        float boundZ = (stepZ > 0 ? (iz + 1) : iz) * voxelSize;

        float tMaxX = (boundX - pos.X) * (stepX > 0 ? invDx : -invDx);
        float tMaxY = (boundY - pos.Y) * (stepY > 0 ? invDy : -invDy);
        float tMaxZ = (boundZ - pos.Z) * (stepZ > 0 ? invDz : -invDz);

        Vector3i lastNormal = Vector3i.Zero;
        var coordSet = new HashSet<Vector3i>(model.VoxelCoordinates);

        for (int i = 0; i < 512; i++)
        {
            var current = new Vector3i(ix, iy, iz);

            if (coordSet.Contains(current))
            {
                hitVoxel  = current;
                hitNormal = lastNormal;
                return true;
            }

            // Выход за пределы модели
            Vector3 worldPos = new Vector3(ix * voxelSize, iy * voxelSize, iz * voxelSize);
            if (worldPos.X > boundsMax.X || worldPos.Y > boundsMax.Y || worldPos.Z > boundsMax.Z ||
                worldPos.X < boundsMin.X - voxelSize || worldPos.Y < boundsMin.Y - voxelSize || worldPos.Z < boundsMin.Z - voxelSize)
                break;

            // DDA шаг
            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                ix       += stepX;
                tMaxX    += tDeltaX;
                lastNormal = new Vector3i(-stepX, 0, 0);
            }
            else if (tMaxY < tMaxZ)
            {
                iy       += stepY;
                tMaxY    += tDeltaY;
                lastNormal = new Vector3i(0, -stepY, 0);
            }
            else
            {
                iz       += stepZ;
                tMaxZ    += tDeltaZ;
                lastNormal = new Vector3i(0, 0, -stepZ);
            }
        }

        return false;
    }
}