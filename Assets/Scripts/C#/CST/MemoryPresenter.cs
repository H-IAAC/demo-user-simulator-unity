using UnityEngine;
using HIAAC.CstUnity.Core.Entities;
using HIAAC.CstUnity.MemoryStorage;
using StackExchange.Redis;

namespace HIAAC.CstUnity.Demo
{
    public class MemoryPresenter : MonoBehaviour
    {
        [Header("Redis / Mind")]
        [SerializeField] private string redisConnectionString = "localhost,abortConnect=false";
        [SerializeField] private string nodeName = "memory_presenter";
        [SerializeField] private string mindName = "default_mind";
        [SerializeField] private int timeStepMs = 100;
        [SerializeField] private bool logOnChange = true;

        private Mind mind;
        private Memory apiConfiguration;
        private Memory sumoConfiguration;
        private Memory gpsBuffer;
        private Memory episodes;
        private MemoryStorageCodelet memoryStorageCodelet;
        private MemoryPresenterCodelet memoryPresenterCodelet;

        private void Start()
        {
            mind = new Mind();
            apiConfiguration = mind.createMemoryObject("ApiConfiguration", "");
            sumoConfiguration = mind.createMemoryObject("SumoConfiguration", "");
            gpsBuffer = mind.createMemoryObject("GPSBuffer", "");
            episodes = mind.createMemoryObject("Episodes", "");

            try
            {
                memoryStorageCodelet = new MemoryStorageCodelet(
                    mind,
                    nodeName,
                    mindName,
                    0.5,
                    redisConnectionString
                );
                memoryStorageCodelet.setTimeStep(timeStepMs);
                mind.insertCodelet(memoryStorageCodelet);
            }
            catch (RedisConnectionException ex)
            {
                Debug.LogError($"[MemoryPresenter] Redis unavailable ({redisConnectionString}). Details: {ex.Message}");
                enabled = false;
                return;
            }

            memoryPresenterCodelet = new MemoryPresenterCodelet(logOnChange);
            memoryPresenterCodelet.setTimeStep(timeStepMs);
            memoryPresenterCodelet.addInput(apiConfiguration);
            memoryPresenterCodelet.addInput(sumoConfiguration);
            memoryPresenterCodelet.addInput(gpsBuffer);
            memoryPresenterCodelet.addInput(episodes);
            mind.insertCodelet(memoryPresenterCodelet);

            mind.start();
        }

        private void OnDestroy()
        {
            mind?.shutDown();
        }

        private class MemoryPresenterCodelet : Codelet
        {
            private readonly bool logOnChange;
            private Memory inputApiConfiguration;
            private Memory inputSumoConfiguration;
            private Memory inputGpsBuffer;
            private Memory inputEpisodes;
            private string lastConfiguration;
            private string lastGps;
            private string lastEpisodes;
            private string lastConfigError;

            public MemoryPresenterCodelet(bool logOnChange)
            {
                this.logOnChange = logOnChange;
            }

            public override void accessMemoryObjects()
            {
                inputApiConfiguration = getInput("ApiConfiguration", 0);
                inputSumoConfiguration = getInput("SumoConfiguration", 0);
                inputGpsBuffer = getInput("GPSBuffer", 0);
                inputEpisodes = getInput("Episodes", 0);
            }

            public override void calculateActivation()
            {
                activation = 1.0f;
            }

            public override void proc()
            {
                if (inputApiConfiguration == null || inputSumoConfiguration == null || inputGpsBuffer == null || inputEpisodes == null)
                    return;

                string api = inputApiConfiguration.getI()?.ToString() ?? "";
                string sumo = inputSumoConfiguration.getI()?.ToString() ?? "";
                string gps = inputGpsBuffer.getI()?.ToString() ?? "";
                string eps = inputEpisodes.getI()?.ToString() ?? "";

                if (!logOnChange)
                    return;

                bool apiEmpty = string.IsNullOrWhiteSpace(api);
                bool sumoEmpty = string.IsNullOrWhiteSpace(sumo);

                if (apiEmpty && sumoEmpty)
                {
                    if (lastConfigError == "waiting" && gps == lastGps && eps == lastEpisodes)
                        return;

                    lastConfigError = "waiting";
                    lastGps = gps;
                    lastEpisodes = eps;
                    Debug.Log($"[MemoryPresenter] Waiting for configuration in Redis | GPSBuffer={gps} | Episodes={eps}");
                    return;
                }

                if (apiEmpty || sumoEmpty)
                {
                    string partialError = apiEmpty ? "ApiConfiguration vazio" : "SumoConfiguration vazio";
                    if (lastConfigError == partialError && gps == lastGps && eps == lastEpisodes)
                        return;

                    lastConfigError = partialError;
                    lastGps = gps;
                    lastEpisodes = eps;
                    Debug.LogWarning($"[MemoryPresenter] Partial configuration from Redis: {partialError} | GPSBuffer={gps} | Episodes={eps}");
                    return;
                }

                if (!ConfigurationContract.TryParseFromRedisPayloads(api, sumo, out ConfigurationContract.RootConfig config, out string configError))
                {
                    if (configError == lastConfigError && gps == lastGps && eps == lastEpisodes)
                        return;

                    lastConfigError = configError;
                    lastGps = gps;
                    lastEpisodes = eps;

                    Debug.LogWarning($"[MemoryPresenter] Invalid configuration from Redis: {configError} | GPSBuffer={gps} | Episodes={eps}");
                    return;
                }

                string fullConfiguration = JsonUtility.ToJson(config);
                if (fullConfiguration == lastConfiguration && gps == lastGps && eps == lastEpisodes)
                    return;

                lastConfiguration = fullConfiguration;
                lastConfigError = null;
                lastGps = gps;
                lastEpisodes = eps;
                Debug.Log($"[MemoryPresenter] Configuration={fullConfiguration} | GPSBuffer={gps} | Episodes={eps}");
            }
        }
    }
}
