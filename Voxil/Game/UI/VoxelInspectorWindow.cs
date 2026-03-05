// --- START OF FILE VoxelInspectorWindow.cs ---
using ImGuiNET;
using OpenTK.Mathematics;
using System;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;

public class VoxelInspectorWindow : IUIWindow
{
    private bool _isVisible = false;
    public bool IsVisible { get => _isVisible; set => _isVisible = value; }

    private readonly IWorldService _worldService;
    private readonly IVoxelEditService _editService;
    private readonly Camera _camera;

    public VoxelInspectorWindow(IWorldService worldService, Camera camera)
    {
        _worldService = worldService;
        _editService  = ServiceLocator.Get<IVoxelEditService>();
        _camera       = camera;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(280, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.7f);

        var flags = ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("Voxel Inspector", ref _isVisible, flags))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "TARGETED VOXEL INFO");
            ImGui.Separator();

            var physics = _worldService.PhysicsWorld;
            var pos = _camera.Position.ToSystemNumerics();
            var dir = _camera.Front.ToSystemNumerics();
            var hit = new VoxelHitHandler
            {
                PlayerBodyHandle = physics.GetPlayerState().BodyHandle,
                Simulation       = physics.Simulation
            };

            physics.Simulation.RayCast(pos, dir, 10f, physics.Simulation.BufferPool, ref hit);

            if (hit.Hit)
            {
                var pointInside = hit.T * dir + pos - hit.Normal * 0.01f;

                if (hit.Collidable.Mobility == BepuPhysics.Collidables.CollidableMobility.Static)
                {
                    Vector3i globalPos = new Vector3i(
                        (int)Math.Floor(pointInside.X),
                        (int)Math.Floor(pointInside.Y),
                        (int)Math.Floor(pointInside.Z));

                    var mat = _editService.GetMaterialGlobal(globalPos);
                    _editService.GetStaticVoxelHealthInfo(globalPos, out float currentHP, out float maxHP);

                    ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "Type: STATIC WORLD");
                    ImGui.Text($"Material: {mat}");
                    ImGui.Text($"Global Pos: {globalPos.X}, {globalPos.Y}, {globalPos.Z}");
                    ImGui.ProgressBar(currentHP / maxHP, new Vector2(-1, 0), $"{currentHP:F1} / {maxHP:F1} HP");
                }
                else
                {
                    var allObjects = ServiceLocator.Get<IVoxelObjectService>().GetAllVoxelObjects();
                    VoxelObject vo = null;
                    foreach (var obj in allObjects)
                    {
                        if (obj.BodyHandle.Value == hit.Collidable.BodyHandle.Value)
                        {
                            vo = obj;
                            break;
                        }
                    }

                    if (vo != null)
                    {
                        Matrix4 invModel = vo.GetInterpolatedModelMatrix(physics.PhysicsAlpha).Inverted();
                        Vector3 localPosRaw = Vector3.TransformPosition(pointInside.ToOpenTK(), invModel);
                        Vector3i localPos = new Vector3i(
                            (int)Math.Floor(localPosRaw.X),
                            (int)Math.Floor(localPosRaw.Y),
                            (int)Math.Floor(localPosRaw.Z));

                        vo.VoxelMaterials.TryGetValue(localPos, out uint mRaw);
                        MaterialType mat = mRaw == 0 ? vo.Material : (MaterialType)mRaw;
                        vo.GetVoxelHealthInfo(localPos, out float currentHP, out float maxHP);

                        ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0, 1), "Type: DYNAMIC OBJECT");
                        ImGui.Text($"Object ID: {vo.GetHashCode()}");
                        ImGui.Text($"Material: {mat}");
                        ImGui.Text($"Local Pos: {localPos.X}, {localPos.Y}, {localPos.Z}");

                        if (mat == MaterialType.TNT)
                            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new System.Numerics.Vector4(1, 0, 0, 1));
                        ImGui.ProgressBar(currentHP / maxHP, new Vector2(-1, 0), $"{currentHP:F1} / {maxHP:F1} HP");
                        if (mat == MaterialType.TNT)
                            ImGui.PopStyleColor();
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("Looking at: Air");
            }
        }
        ImGui.End();
    }
}