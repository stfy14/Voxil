using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System.Collections.Generic;

public class PhysicsDebugDrawer
{
    public void DrawVoxelObjects(PhysicsWorld physicsWorld, IEnumerable<VoxelObject> voxelObjects, LineRenderer lineRenderer)
    {
        var sim = physicsWorld.Simulation;

        foreach (var vo in voxelObjects)
        {
            if (!sim.Bodies.BodyExists(vo.BodyHandle)) continue;

            var bodyRef = sim.Bodies.GetBodyReference(vo.BodyHandle);
            var shapeIndex = bodyRef.Collidable.Shape;
            ref var compound = ref sim.Shapes.GetShape<Compound>(shapeIndex.Index);

            // Получаем мировую трансформацию тела
            var bodyPos = bodyRef.Pose.Position.ToOpenTK();
            var bodyRot = bodyRef.Pose.Orientation.ToOpenTK();

            for (int i = 0; i < compound.ChildCount; ++i)
            {
                ref var child = ref compound.Children[i];

                // Локальная позиция чайлда относительно тела
                var childLocalPos = child.LocalPosition.ToOpenTK();
                
                // Мировая позиция центра вокселя
                // Pos = BodyPos + (BodyRot * ChildLocalPos)
                var childWorldPos = bodyPos + Vector3.Transform(childLocalPos, bodyRot);

                // Размер половинки вокселя
                float h = Constants.VoxelSize / 2.0f;

                // 8 углов куба (локально, без вращения)
                Vector3[] corners = new Vector3[8]
                {
                    new Vector3(-h, -h, -h), new Vector3( h, -h, -h),
                    new Vector3( h,  h, -h), new Vector3(-h,  h, -h),
                    new Vector3(-h, -h,  h), new Vector3( h, -h,  h),
                    new Vector3( h,  h,  h), new Vector3(-h,  h,  h)
                };

                // Вращаем углы и сдвигаем к центру
                for (int k = 0; k < 8; k++)
                {
                    corners[k] = childWorldPos + Vector3.Transform(corners[k], bodyRot);
                }

                // Рисуем линии между углами
                DrawRotatedBox(lineRenderer, corners, new Vector3(0, 1, 0));
            }

            // Центр масс
            lineRenderer.DrawPoint(bodyPos, Constants.VoxelSize, new Vector3(1, 1, 0));
        }
    }

    private void DrawRotatedBox(LineRenderer lr, Vector3[] c, Vector3 color)
    {
        // Нижняя грань
        lr.DrawLine(c[0], c[1], color); lr.DrawLine(c[1], c[2], color);
        lr.DrawLine(c[2], c[3], color); lr.DrawLine(c[3], c[0], color);
        // Верхняя грань
        lr.DrawLine(c[4], c[5], color); lr.DrawLine(c[5], c[6], color);
        lr.DrawLine(c[6], c[7], color); lr.DrawLine(c[7], c[4], color);
        // Стойки
        lr.DrawLine(c[0], c[4], color); lr.DrawLine(c[1], c[5], color);
        lr.DrawLine(c[2], c[6], color); lr.DrawLine(c[3], c[7], color);
    }
}