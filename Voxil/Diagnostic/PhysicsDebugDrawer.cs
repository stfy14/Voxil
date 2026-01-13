using BepuPhysics;
using BepuPhysics.Collidables;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

using BepuVector3 = System.Numerics.Vector3;
using TkVector3 = OpenTK.Mathematics.Vector3;

public class PhysicsDebugDrawer
{
    private readonly FrustumCuller _culler = new FrustumCuller();

    public void Draw(PhysicsWorld physicsWorld, LineRenderer lineRenderer, Camera camera)
    {
        var sim = physicsWorld.Simulation;
        var mode = GameSettings.DebugCollisionMode;
        
        if (mode == CollisionDebugMode.None) return;

        var playerHandle = physicsWorld.GetPlayerState().BodyHandle;
        var camPos = camera.Position;

        _culler.Update(camera.GetViewMatrix() * camera.GetProjectionMatrix());

        // ==========================================
        // ПРОХОД 1: СТАТИКА (С включенным Depth Test)
        // ==========================================
        if (mode == CollisionDebugMode.StaticOnly || mode == CollisionDebugMode.All)
        {
            float detailRangeSq = 15 * 15; 

            for (int i = 0; i < sim.Statics.Count; i++)
            {
                var handle = sim.Statics.IndexToHandle[i];
                var staticRef = sim.Statics.GetStaticReference(handle);
                
                GetShapeBounds(sim, staticRef.Shape, staticRef.Pose, out TkVector3 min, out TkVector3 max);
                
                if (!_culler.IsBoxVisible(min, max)) continue;

                TkVector3 center = (min + max) * 0.5f;
                float distSq = (center - camPos).LengthSquared;

                // ИЗМЕНЕНИЕ: Если чанк дальше 15 метров - вообще ничего не рисуем.
                // Синие рамки удалены.
                if (distSq > detailRangeSq) continue;

                // Рисуем детали (вокесли)
                DrawShape(sim, staticRef.Shape, staticRef.Pose, lineRenderer, new TkVector3(0, 1, 1), camPos, true); 
            }

            lineRenderer.Render(camera, enableDepthTest: true);
        }

        // ==========================================
        // ПРОХОД 2: ДИНАМИКА (Без Depth Test - видно сквозь стены)
        // ==========================================
        if (mode == CollisionDebugMode.PhysicsOnly || mode == CollisionDebugMode.All)
        {
            for (int i = 0; i < sim.Bodies.ActiveSet.Count; i++)
            {
                var handle = sim.Bodies.ActiveSet.IndexToHandle[i];
                if (handle == playerHandle) continue;

                var bodyRef = sim.Bodies.GetBodyReference(handle);
                
                GetShapeBounds(sim, bodyRef.Collidable.Shape, bodyRef.Pose, out TkVector3 min, out TkVector3 max);
                
                if (!_culler.IsBoxVisible(min, max)) continue;

                DrawBody(sim, handle, lineRenderer, new TkVector3(1, 0.5f, 0)); // Оранжевый
            }

            lineRenderer.Render(camera, enableDepthTest: false);
        }
    }

    // --- Остальные методы (GetShapeBounds, DrawBody, DrawShape, DrawBoxShape, DrawWireBox, FrustumCuller) 
    // --- ОСТАЮТСЯ БЕЗ ИЗМЕНЕНИЙ, скопируй их из предыдущего ответа ---
    
    private void GetShapeBounds(Simulation sim, TypedIndex shapeIndex, RigidPose pose, out TkVector3 min, out TkVector3 max)
    {
        BepuVector3 pMin, pMax;
        if (shapeIndex.Type == Compound.TypeId) { ref var shape = ref sim.Shapes.GetShape<Compound>(shapeIndex.Index); shape.ComputeBounds(pose.Orientation, sim.Shapes, out pMin, out pMax); }
        else if (shapeIndex.Type == Box.TypeId) { ref var shape = ref sim.Shapes.GetShape<Box>(shapeIndex.Index); shape.ComputeBounds(pose.Orientation, out pMin, out pMax); }
        else { pMin = new BepuVector3(-0.5f); pMax = new BepuVector3(0.5f); }
        min = (pMin + pose.Position).ToOpenTK(); max = (pMax + pose.Position).ToOpenTK();
    }

    private void DrawBody(Simulation sim, BodyHandle handle, LineRenderer lr, TkVector3 color)
    {
        var bodyRef = sim.Bodies.GetBodyReference(handle);
        DrawShape(sim, bodyRef.Collidable.Shape, bodyRef.Pose, lr, color, TkVector3.Zero, false);
    }

    private void DrawShape(Simulation sim, TypedIndex shapeIndex, RigidPose pose, LineRenderer lr, TkVector3 color, TkVector3 camPos, bool useDistanceCulling)
    {
        var pos = pose.Position.ToOpenTK();
        var rot = pose.Orientation.ToOpenTK();

        if (shapeIndex.Type == Compound.TypeId)
        {
            ref var compound = ref sim.Shapes.GetShape<Compound>(shapeIndex.Index);
            for (int k = 0; k < compound.ChildCount; ++k)
            {
                ref var child = ref compound.Children[k];
                var childLocalPos = child.LocalPosition.ToOpenTK();
                var childWorldPos = pos + TkVector3.Transform(childLocalPos, rot);
                if (useDistanceCulling) { if ((childWorldPos - camPos).LengthSquared > 12 * 12) continue; }
                DrawBoxShape(sim, child.ShapeIndex, childWorldPos, rot, lr, color);
            }
        }
        else { DrawBoxShape(sim, shapeIndex, pos, rot, lr, color); }
    }

    private void DrawBoxShape(Simulation sim, TypedIndex shapeIndex, TkVector3 pos, Quaternion rot, LineRenderer lr, TkVector3 color)
    {
        if (shapeIndex.Type == Box.TypeId) { ref var box = ref sim.Shapes.GetShape<Box>(shapeIndex.Index); TkVector3 halfSize = new TkVector3(box.HalfWidth, box.HalfHeight, box.HalfLength); DrawWireBox(lr, pos, rot, halfSize, color); }
        else if (shapeIndex.Type == Capsule.TypeId) { ref var cap = ref sim.Shapes.GetShape<Capsule>(shapeIndex.Index); TkVector3 halfSize = new TkVector3(cap.Radius, cap.Length * 0.5f + cap.Radius, cap.Radius); DrawWireBox(lr, pos, rot, halfSize, new TkVector3(0, 1, 0)); }
    }

    private void DrawWireBox(LineRenderer lr, TkVector3 center, Quaternion rotation, TkVector3 halfSize, TkVector3 color)
    {
        TkVector3[] corners = new TkVector3[8];
        corners[0] = new TkVector3(-1, -1, -1); corners[1] = new TkVector3(1, -1, -1); corners[2] = new TkVector3(1, 1, -1); corners[3] = new TkVector3(-1, 1, -1);
        corners[4] = new TkVector3(-1, -1, 1); corners[5] = new TkVector3(1, -1, 1); corners[6] = new TkVector3(1, 1, 1); corners[7] = new TkVector3(-1, 1, 1);
        for (int i = 0; i < 8; i++) { TkVector3 local = corners[i] * halfSize; corners[i] = center + TkVector3.Transform(local, rotation); }
        lr.DrawLine(corners[0], corners[1], color); lr.DrawLine(corners[1], corners[2], color); lr.DrawLine(corners[2], corners[3], color); lr.DrawLine(corners[3], corners[0], color);
        lr.DrawLine(corners[4], corners[5], color); lr.DrawLine(corners[5], corners[6], color); lr.DrawLine(corners[6], corners[7], color); lr.DrawLine(corners[7], corners[4], color);
        lr.DrawLine(corners[0], corners[4], color); lr.DrawLine(corners[1], corners[5], color); lr.DrawLine(corners[2], corners[6], color); lr.DrawLine(corners[3], corners[7], color);
    }

    private class FrustumCuller
    {
        private readonly Vector4[] _planes = new Vector4[6];
        public void Update(Matrix4 viewProj)
        {
            _planes[0] = new Vector4(viewProj.M14 + viewProj.M11, viewProj.M24 + viewProj.M21, viewProj.M34 + viewProj.M31, viewProj.M44 + viewProj.M41);
            _planes[1] = new Vector4(viewProj.M14 - viewProj.M11, viewProj.M24 - viewProj.M21, viewProj.M34 - viewProj.M31, viewProj.M44 - viewProj.M41);
            _planes[2] = new Vector4(viewProj.M14 + viewProj.M12, viewProj.M24 + viewProj.M22, viewProj.M34 + viewProj.M32, viewProj.M44 + viewProj.M42);
            _planes[3] = new Vector4(viewProj.M14 - viewProj.M12, viewProj.M24 - viewProj.M22, viewProj.M34 - viewProj.M32, viewProj.M44 - viewProj.M42);
            _planes[4] = new Vector4(viewProj.M14 + viewProj.M13, viewProj.M24 + viewProj.M23, viewProj.M34 + viewProj.M33, viewProj.M44 + viewProj.M43);
            _planes[5] = new Vector4(viewProj.M14 - viewProj.M13, viewProj.M24 - viewProj.M23, viewProj.M34 - viewProj.M33, viewProj.M44 - viewProj.M43);
            for (int i = 0; i < 6; i++) { float length = new TkVector3(_planes[i].X, _planes[i].Y, _planes[i].Z).Length; _planes[i] /= length; }
        }
        public bool IsBoxVisible(TkVector3 min, TkVector3 max)
        {
            for (int i = 0; i < 6; i++) {
                TkVector3 p; p.X = _planes[i].X > 0 ? max.X : min.X; p.Y = _planes[i].Y > 0 ? max.Y : min.Y; p.Z = _planes[i].Z > 0 ? max.Z : min.Z;
                if (Vector4.Dot(_planes[i], new Vector4(p, 1.0f)) < 0) return false;
            }
            return true;
        }
    }
}