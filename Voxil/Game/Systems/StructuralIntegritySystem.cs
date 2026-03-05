using OpenTK.Mathematics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

public class StructuralIntegritySystem : IDisposable
{
    private struct IntegrityCheckTask { public Vector3i GlobalPosition; }

    private readonly BlockingCollection<IntegrityCheckTask> _queue = new();
    private readonly Thread _workerThread;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    // Сервисы берём из ServiceLocator в момент использования,
    // чтобы не создавать зависимость на момент конструирования
    private IVoxelEditService EditService   => ServiceLocator.Get<IVoxelEditService>();
    private IVoxelObjectService ObjService  => ServiceLocator.Get<IVoxelObjectService>();
    private IWorldService WorldService      => ServiceLocator.Get<IWorldService>();

    public StructuralIntegritySystem()
    {
        EventBus.Subscribe<IntegrityCheckEvent>(e => QueueCheck(e.GlobalPosition));
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
        if (!_isDisposed && !_cts.IsCancellationRequested && !_queue.IsAddingCompleted)
        {
            try { _queue.Add(new IntegrityCheckTask { GlobalPosition = globalPos }); }
            catch (InvalidOperationException) { }
        }
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
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
            catch (Exception ex) { Console.WriteLine($"[Integrity] Error: {ex.Message}"); }
        }
    }

    private void CheckNeighbors(Vector3i destroyedPos)
    {
        var neighbors = new Vector3i[]
        {
            destroyedPos + new Vector3i( 1, 0, 0), destroyedPos + new Vector3i(-1, 0, 0),
            destroyedPos + new Vector3i( 0, 1, 0), destroyedPos + new Vector3i( 0,-1, 0),
            destroyedPos + new Vector3i( 0, 0, 1), destroyedPos + new Vector3i( 0, 0,-1)
        };

        var checkedGlobals = new HashSet<Vector3i> { destroyedPos };

        foreach (var neighbor in neighbors)
        {
            if (checkedGlobals.Contains(neighbor)) continue;
            if (!EditService.IsVoxelSolidGlobal(neighbor)) continue;
            TraverseCluster(neighbor, checkedGlobals);
        }
    }

    private void TraverseCluster(Vector3i startNode, HashSet<Vector3i> globalChecked)
    {
        var cluster = new List<Vector3i>();
        var queue   = new Queue<Vector3i>();
        var visited = new HashSet<Vector3i>();

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

            if (cluster.Count > maxClusterSize)
            {
                Console.WriteLine($"[Integrity] Cluster too large ({cluster.Count}+), keeping static.");
                isGrounded = true;
                break;
            }

            var dirs = new Vector3i[]
            {
                new(1,0,0), new(-1,0,0),
                new(0,1,0), new( 0,-1,0),
                new(0,0,1), new( 0, 0,-1)
            };

            foreach (var d in dirs)
            {
                var next = current + d;
                if (visited.Contains(next)) continue;

                if (EditService.IsVoxelSolidGlobal(next))
                {
                    visited.Add(next);
                    globalChecked.Add(next);
                    cluster.Add(next);
                    queue.Enqueue(next);
                }
                else if (!WorldService.IsChunkLoadedAt(next))
                {
                    isGrounded = true;
                    break;
                }
            }
            if (isGrounded) break;
        }

        if (!isGrounded && cluster.Count > 0)
            ObjService.CreateDetachedObject(cluster);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts.Cancel();
        if (_workerThread.IsAlive) _workerThread.Join(200);
        _queue.Dispose();
        _cts.Dispose();
    }
}