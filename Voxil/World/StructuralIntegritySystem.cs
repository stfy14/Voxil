using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

public class StructuralIntegritySystem : IDisposable
{
    private struct IntegrityCheckTask { public Vector3i GlobalPosition; }

    private readonly WorldManager _worldManager;
    private readonly BlockingCollection<IntegrityCheckTask> _queue = new();
    private readonly Thread _workerThread;
    private readonly CancellationTokenSource _cts = new();

    public StructuralIntegritySystem(WorldManager worldManager)
    {
        _worldManager = worldManager;
        _workerThread = new Thread(WorkerLoop) 
        { 
            IsBackground = true, 
            Name = "IntegritySystem", 
            Priority = ThreadPriority.Lowest 
        };
        _workerThread.Start();
    }

    public void QueueCheck(Vector3i globalPos)
    {
        if (!_cts.IsCancellationRequested)
            _queue.Add(new IntegrityCheckTask { GlobalPosition = globalPos });
    }

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var task = _queue.Take(_cts.Token);
                CheckNeighbors(task.GlobalPosition);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[Integrity] Error: {ex.Message}"); }
        }
    }

    private void CheckNeighbors(Vector3i destroyedPos)
    {
        var neighbors = new Vector3i[] {
            destroyedPos + new Vector3i(1,0,0), destroyedPos + new Vector3i(-1,0,0),
            destroyedPos + new Vector3i(0,1,0), destroyedPos + new Vector3i(0,-1,0),
            destroyedPos + new Vector3i(0,0,1), destroyedPos + new Vector3i(0,0,-1)
        };

        HashSet<Vector3i> checkedGlobals = new HashSet<Vector3i>();
        checkedGlobals.Add(destroyedPos);

        foreach (var neighbor in neighbors)
        {
            if (checkedGlobals.Contains(neighbor)) continue;
            
            // Проверка через WorldManager
            if (!_worldManager.IsVoxelSolidGlobal(neighbor)) continue;

            TraverseCluster(neighbor, checkedGlobals);
        }
    }

    private void TraverseCluster(Vector3i startNode, HashSet<Vector3i> globalChecked)
    {
        List<Vector3i> cluster = new List<Vector3i>();
        Queue<Vector3i> queue = new Queue<Vector3i>();
        HashSet<Vector3i> visited = new HashSet<Vector3i>();

        queue.Enqueue(startNode);
        visited.Add(startNode);
        cluster.Add(startNode);
        globalChecked.Add(startNode);

        bool isGrounded = false;
        int maxClusterSize = 512;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current.Y <= 0) { isGrounded = true; break; }
            if (cluster.Count > maxClusterSize) { isGrounded = true; break; }

            var dirs = new Vector3i[] { new(1,0,0), new(-1,0,0), new(0,1,0), new(0,-1,0), new(0,0,1), new(0,0,-1) };

            foreach (var d in dirs)
            {
                var next = current + d;
                if (visited.Contains(next)) continue;

                // Проверяем: либо блок твердый, либо это земля (незагруженный чанк считается опорой)
                if (_worldManager.IsVoxelSolidGlobal(next))
                {
                    visited.Add(next);
                    globalChecked.Add(next);
                    cluster.Add(next);
                    queue.Enqueue(next);
                }
                else if (!_worldManager.IsChunkLoadedAt(next))
                {
                    // Если уперлись в незагруженный чанк — считаем, что остров держится за него
                    isGrounded = true;
                    break;
                }
            }
            if (isGrounded) break;
        }

        if (!isGrounded && cluster.Count > 0)
        {
            _worldManager.CreateDetachedObject(cluster);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Dispose();
        _workerThread.Join(100);
    }
}