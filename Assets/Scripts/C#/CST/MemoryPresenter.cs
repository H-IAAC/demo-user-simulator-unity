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
        private Memory gpsBuffer;
        private Memory episodes;
        private MemoryStorageCodelet memoryStorageCodelet;
        private MemoryPresenterCodelet memoryPresenterCodelet;

        private void Start()
        {
            mind = new Mind();
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
            private Memory inputGpsBuffer;
            private Memory inputEpisodes;
            private string lastGps;
            private string lastEpisodes;

            public MemoryPresenterCodelet(bool logOnChange)
            {
                this.logOnChange = logOnChange;
            }

            public override void accessMemoryObjects()
            {
                inputGpsBuffer = getInput("GPSBuffer", 0);
                inputEpisodes = getInput("Episodes", 0);
            }

            public override void calculateActivation()
            {
                activation = 1.0f;
            }

            public override void proc()
            {
                if (inputGpsBuffer == null || inputEpisodes == null)
                    return;

                string gps = inputGpsBuffer.getI()?.ToString() ?? "";
                string eps = inputEpisodes.getI()?.ToString() ?? "";

                if (!logOnChange)
                    return;

                if (gps == lastGps && eps == lastEpisodes)
                    return;

                lastGps = gps;
                lastEpisodes = eps;
                Debug.Log($"[MemoryPresenter] GPSBuffer={gps} | Episodes={eps}");
            }
        }
    }
}
