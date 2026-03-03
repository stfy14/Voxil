// --- START OF FILE MathUtils.cs ---
using OpenTK.Mathematics;
using System;

public static class MathUtils
{
    /// <summary>
    /// Рассчитывает матрицу для объекта, который должен быть жестко привязан к камере (View Model).
    /// </summary>
    /// <param name="cameraViewMatrix">Матрица вида камеры (Camera.GetViewMatrix())</param>
    /// <param name="handOffset">Смещение от глаз (вправо, вниз, вперед)</param>
    /// <param name="itemTilt">Локальный поворот самого предмета</param>
    /// <param name="bobbingOffset">Смещение от ходьбы (если есть)</param>
    public static void CalculateViewModelTransform(
        Matrix4 cameraViewMatrix, 
        Vector3 handOffset, 
        Quaternion itemTilt, 
        Vector3 bobbingOffset,
        out Vector3 worldPos, 
        out Quaternion worldRot)
    {
        // 1. Получаем матрицу мира камеры (инверсия ViewMatrix)
        Matrix4 cameraWorld = cameraViewMatrix.Inverted();

        // 2. Итоговое локальное смещение (база + покачивание)
        Vector3 finalOffset = handOffset + bobbingOffset;

        // 3. Создаем ЛОКАЛЬНУЮ матрицу предмета
        // Сначала поворачиваем сам предмет (Tilt), потом сдвигаем в позицию руки
        Matrix4 itemLocal = Matrix4.CreateFromQuaternion(itemTilt) * Matrix4.CreateTranslation(finalOffset);

        // 4. ФИНАЛЬНАЯ МАТРИЦА: Применяем трансформацию камеры к локальной матрице предмета
        Matrix4 finalWorld = itemLocal * cameraWorld;

        // 5. Извлекаем результаты
        worldPos = finalWorld.ExtractTranslation();
        worldRot = finalWorld.ExtractRotation();
    }
}