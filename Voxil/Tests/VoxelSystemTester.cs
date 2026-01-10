using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using OpenTK.Mathematics;

public class VoxelSystemTester
{
    private WorldManager _testWorld;
    private GpuRaycastingRenderer _testRenderer;
    
    // Вспомогательные поля для доступа к привату
    private FieldInfo _allocatedChunksField;
    private FieldInfo _activeBankCountField;
    private FieldInfo _freeSlotsField;

    public void RunStressTest()
    {
        Console.WriteLine("\n=== STARTING VOXEL SYSTEM STRESS TEST ===");
        
        // 1. Создаем изолированную среду (без физики и генераторов, только логика памяти)
        // Нам нужен WorldManager, но мы не будем запускать его Update loop.
        _testWorld = new WorldManager(null, null); // Передай null, если конструктор позволяет, или заглушки
        _testRenderer = new GpuRaycastingRenderer(_testWorld);
        
        // Инициализируем GPU буферы (это создаст 1 банку)
        _testRenderer.InitializeBuffers();

        // Подключаемся к приватным полям через рефлексию
        Type type = typeof(GpuRaycastingRenderer);
        _allocatedChunksField = type.GetField("_allocatedChunks", BindingFlags.NonPublic | BindingFlags.Instance);
        _activeBankCountField = type.GetField("_activeBankCount", BindingFlags.NonPublic | BindingFlags.Instance);
        _freeSlotsField = type.GetField("_freeSlots", BindingFlags.NonPublic | BindingFlags.Instance);

        try
        {
            Test1_SolidChunkOptimization();
            Test2_BankOverflow();
            Test3_SlotRecycling();
            
            Console.WriteLine("=== ALL TESTS PASSED SUCCESSFULLY! SYSTEM IS STABLE. ===\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!!! TEST FAILED: {ex.Message} !!!");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // Чистим за собой, чтобы не засорять VRAM
            _testRenderer.Dispose();
            _testWorld.Dispose();
            Console.WriteLine("=== Test Environment Disposed ===\n");
        }
    }

    private void Test1_SolidChunkOptimization()
    {
        Console.WriteLine("[Test 1] Testing Solid/Air Optimization...");
        
        // Создаем чанк, который ПОЛНОСТЬЮ заполнен камнем
        Chunk solidChunk = new Chunk(new Vector3i(100, 0, 0), _testWorld);
        var fullData = new MaterialType[Chunk.Volume];
        Array.Fill(fullData, MaterialType.Stone);
        solidChunk.SetDataFromArray(fullData);

        // Скармливаем рендеру
        _testRenderer.NotifyChunkLoaded(solidChunk);

        // ПРОВЕРКИ:
        // 1. Он не должен занять слот в SSBO
        var allocs = (Dictionary<Vector3i, int>)_allocatedChunksField.GetValue(_testRenderer);
        if (allocs.ContainsKey(solidChunk.Position))
        {
            int slot = allocs[solidChunk.Position];
            // В твоем коде -1 это SOLITARY_SLOT_INDEX (или другая константа для Solid)
            if (slot != -1) throw new Exception($"Solid chunk took a VRAM slot! Slot: {slot}");
        }
        else
        {
            // Если ты не хранишь solid чанки в словаре _allocatedChunks, то это ок.
            // Но обычно они там лежат с индексом -1.
        }

        Console.WriteLine("PASS: Solid chunk handled correctly (No VRAM usage).");
    }

    private void Test2_BankOverflow()
    {
        Console.WriteLine("[Test 2] Testing Bank Overflow & Expansion...");

        int chunksToLoad = 70000; // Это гарантированно больше, чем вмещает 1 банка (63-65к)
        int initialBanks = (int)_activeBankCountField.GetValue(_testRenderer);
        
        Console.WriteLine($"Loading {chunksToLoad} non-uniform chunks...");
        
        // Генерируем "сложный" чанк (шахматная доска), чтобы он не прошел проверку на Uniform
        var messyData = new MaterialType[Chunk.Volume];
        messyData[0] = MaterialType.Stone; 
        messyData[1] = MaterialType.Dirt; 

        // Загружаем 70к виртуальных чанков
        for (int i = 0; i < chunksToLoad; i++)
        {
            Vector3i pos = new Vector3i(i, 100, 0); // Уникальная позиция
            Chunk c = new Chunk(pos, _testWorld);
            c.SetDataFromArray(messyData);
            _testRenderer.NotifyChunkLoaded(c);
            
            // Важно: в твоем Update есть очередь _uploadQueue. 
            // Тест не вызывает Update, но NotifyChunkLoaded выделяет слот МГНОВЕННО.
            // Так что мы тестируем именно логику аллокатора.
        }

        int finalBanks = (int)_activeBankCountField.GetValue(_testRenderer);
        var allocs = (Dictionary<Vector3i, int>)_allocatedChunksField.GetValue(_testRenderer);

        Console.WriteLine($"Initial Banks: {initialBanks}, Final Banks: {finalBanks}");
        Console.WriteLine($"Total allocated slots: {allocs.Count}");

        if (finalBanks <= initialBanks) 
            throw new Exception("Allocator failed to expand banks! Still on bank 1.");
            
        if (allocs.Count != chunksToLoad + 1) // +1 от прошлого теста
             throw new Exception($"Allocation mismatch! Expected {chunksToLoad + 1}, got {allocs.Count}");

        Console.WriteLine("PASS: System successfully expanded to Bank 2.");
    }

    private void Test3_SlotRecycling()
{
    Console.WriteLine("[Test 3] Testing Slot Recycling (Fragmentation)...");
    
    // 1. Удаляем 1000 чанков
    var allocs = (Dictionary<Vector3i, int>)_allocatedChunksField.GetValue(_testRenderer);
    // Делаем копию ключей, чтобы не сломать итератор словаря при удалении
    var keys = new List<Vector3i>(allocs.Keys); 
    int deletedCount = 0;
    
    // Пытаемся удалить до 1000 чанков (проверяем bounds на случай если ключей меньше)
    int limit = Math.Min(keys.Count, 1000);
    for(int i=0; i < limit; i++)
    {
        // Просто удаляем первые попавшиеся, не важно какие (Test 2 насоздавал их 70к)
        // Главное чтобы это не были Solid (index -1), но в Test 2 мы создавали messyData, они не solid.
        if (allocs[keys[i]] != -1) 
        {
            _testRenderer.UnloadChunk(keys[i]);
            deletedCount++;
        }
    }

    // 2. Проверяем очередь свободных слотов
    var freeSlotsQueue = (Queue<int>)_freeSlotsField.GetValue(_testRenderer);
    
    // !!! ВАЖНОЕ ИСПРАВЛЕНИЕ !!!
    // Запоминаем ЧИСЛО элементов до начала аллокации.
    int countBeforeAllocation = freeSlotsQueue.Count; 

    Console.WriteLine($"Deleted {deletedCount} chunks. Free slots available: {countBeforeAllocation}.");
    
    if (countBeforeAllocation < deletedCount)
         throw new Exception($"Memory Leak! Deleted {deletedCount} chunks, but queue only has {countBeforeAllocation}");

    // 3. Загружаем 500 новых. 
    int banksBefore = (int)_activeBankCountField.GetValue(_testRenderer);
    
    var messyData = new MaterialType[Chunk.Volume];
    messyData[0] = MaterialType.Wood; // Делаем чанк неоднородным

    int chunksToAllocate = 500;
    for (int i = 0; i < chunksToAllocate; i++)
    {
        // Y=200, чтобы точно не пересечься с предыдущими
        Chunk c = new Chunk(new Vector3i(i, 200, 0), _testWorld);
        c.SetDataFromArray(messyData);
        _testRenderer.NotifyChunkLoaded(c);
    }

    int banksAfter = (int)_activeBankCountField.GetValue(_testRenderer);
    
    // Получаем актуальное количество
    int countAfterAllocation = freeSlotsQueue.Count; 

    // Проверки
    if (banksAfter != banksBefore)
         throw new Exception("Allocator expanded banks unnecessarily instead of reusing slots!");
         
    // Теперь математика сойдется: Было 62069, вставили 500, должно стать 61569.
    if (countAfterAllocation != countBeforeAllocation - chunksToAllocate)
         throw new Exception($"Allocator did not take from free queue correctly! Before: {countBeforeAllocation}, After: {countAfterAllocation}, Diff: {countBeforeAllocation - countAfterAllocation}");

    Console.WriteLine("PASS: System correctly reused memory slots.");
}
}