using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System.Collections.Generic;

public class PhysicsDebugDrawer
{
    // Рисуем конкретные воксельные объекты
    public void DrawVoxelObjects(PhysicsWorld physicsWorld, IEnumerable<VoxelObject> voxelObjects, LineRenderer lineRenderer)
    {
        var sim = physicsWorld.Simulation;

        foreach (var vo in voxelObjects)
        {
            if (!sim.Bodies.BodyExists(vo.BodyHandle)) continue;

            var bodyRef = sim.Bodies.GetBodyReference(vo.BodyHandle);
            var shapeIndex = bodyRef.Collidable.Shape;

            // ВАЖНО: Мы знаем, что VoxelObject использует Compound, поэтому сразу запрашиваем его.
            // Если в будущем тип изменится, здесь будет исключение, что хорошо для отладки.
            ref var compound = ref sim.Shapes.GetShape<Compound>(shapeIndex.Index);

            for (int i = 0; i < compound.ChildCount; ++i)
            {
                ref var child = ref compound.Children[i];

                // Расчет мировой позиции ребенка
                // WorldPos = BodyPos + (BodyRot * ChildLocalPos)
                var childWorldPos = bodyRef.Pose.Position +
                                    BepuUtilities.QuaternionEx.Transform(child.LocalPosition, bodyRef.Pose.Orientation);

                Vector3 center = childWorldPos.ToOpenTK();
                Vector3 size = new Vector3(0.5f); // Половина размера вокселя

                lineRenderer.DrawBox(center - size, center + size, new Vector3(0, 1, 0));
            }

            // Рисуем центр масс (желтый крест)
            lineRenderer.DrawPoint(bodyRef.Pose.Position.ToOpenTK(), 0.5f, new Vector3(1, 1, 0));
        }
    }
}