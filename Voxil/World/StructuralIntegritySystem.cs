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
    private bool _isDisposed;

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
        if (!_isDisposed && !_cts.IsCancellationRequested && !_queue.IsAddingCompleted)
        {
            try
            {
                _queue.Add(new IntegrityCheckTask { GlobalPosition = globalPos });
            }
            catch (InvalidOperationException) { } // Очередь закрывается
        }
    }

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Take блокирует поток до появления задачи или отмены токена
                var task = _queue.Take(_cts.Token);
                CheckNeighbors(task.GlobalPosition);
            }
            catch (OperationCanceledException)
            {
                // Это НОРМАЛЬНЫЙ выход при остановке игры. Просто прерываем цикл.
                break;
            }
            catch (ObjectDisposedException) { break; } // Если очередь уничтожена
            catch (InvalidOperationException) { break; } // Если очередь помечена как завершенная
            catch (Exception ex)
            {
                Console.WriteLine($"[Integrity] Error: {ex.Message}");
            }
        }
    }

    // ... методы CheckNeighbors и TraverseCluster оставляем БЕЗ ИЗМЕНЕНИЙ ...
    // ... (скопируйте их из вашего старого файла, они были корректны) ...
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

            var dirs = new Vector3i[] { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };

            foreach (var d in dirs)
            {
                var next = current + d;
                if (visited.Contains(next)) continue;

                if (_worldManager.IsVoxelSolidGlobal(next))
                {
                    visited.Add(next);
                    globalChecked.Add(next);
                    cluster.Add(next);
                    queue.Enqueue(next);
                }
                else if (!_worldManager.IsChunkLoadedAt(next))
                {
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
        if (_isDisposed) return;
        _isDisposed = true;

        // 1. Отменяем токен. Это вызовет OperationCanceledException в Take()
        _cts.Cancel();

        // 2. Ждем пока поток выйдет из цикла
        if (_workerThread.IsAlive)
            _workerThread.Join(200);

        // 3. Теперь безопасно убиваем очередь и токен
        _queue.Dispose();
        _cts.Dispose();
    }
}