using UnityEngine;

namespace HIAAC.CstUnity.Demo
{
    // Exemplo simples para ler o MemoryStorage e imprimir no console.
    public class MemoryStorageDebugExample : MonoBehaviour
    {
        [SerializeField] private MemoryStorage memoryStorage;
        [SerializeField] private float logIntervalSeconds = 1.0f;

        private float nextLogTime;

        private void Start()
        {
            if (memoryStorage == null)
            {
                memoryStorage = FindAnyObjectByType<MemoryStorage>();
            }

            if (memoryStorage == null)
            {
                Debug.LogError("[MemoryStorageDebugExample] MemoryStorage não encontrado na cena.");
                enabled = false;
                return;
            }

            Debug.Log("[MemoryStorageDebugExample] Iniciado. Lendo memórias periodicamente.");
            LogMemoryValues();
        }

        private void Update()
        {
            if (Time.time < nextLogTime)
                return;

            LogMemoryValues();
            nextLogTime = Time.time + logIntervalSeconds;
        }

        private void LogMemoryValues()
        {
            var sumConfiguration = memoryStorage.GetSumConfiguration();
            var gpsBuffer = memoryStorage.GetGPSBuffer();
            var episodes = memoryStorage.GetEpisodes();

            Debug.Log(
                "[MemoryStorageDebugExample] " +
                $"SumConfiguration={FormatValue(sumConfiguration)} | " +
                $"GPSBuffer={FormatValue(gpsBuffer)} | " +
                $"Episodes={FormatValue(episodes)}"
            );
        }

        private static string FormatValue(object value)
        {
            return value == null ? "<null>" : value.ToString();
        }
    }
}
