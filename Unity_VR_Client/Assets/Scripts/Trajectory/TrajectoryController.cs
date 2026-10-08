using UnityEngine;
using System.Collections.Generic;

public class TrajectoryController : MonoBehaviour
{
    [SerializeField] private VMDClient client;
    [SerializeField, Min(0.01f)] private float stepInterval = 0.1f;
    private VMDClient subscribedClient;
    private bool isPlaying;
    private bool waitingForCoordinates;
    private float nextStepTime;
    private float requestTime;

    private void OnEnable()
    {
        if (client == null)
            client = GetComponent<VMDClient>();
        if (client == null)
            client = FindAnyObjectByType<VMDClient>();

        subscribedClient = client;
        if (subscribedClient != null)
            subscribedClient.CoordinatesReceived += OnCoordinatesReceived;
    }

    private void Update()
    {
        if (subscribedClient == null || !subscribedClient.IsConnected)
        {
            isPlaying = false;
            waitingForCoordinates = false;
            return;
        }

        if (waitingForCoordinates)
        {
            if (Time.unscaledTime - requestTime > 10f)
            {
                isPlaying = false;
                waitingForCoordinates = false;
                Debug.LogWarning("[TrajectoryController] Coordinate response timed out; playback paused.", this);
            }
            return;
        }

        if (!isPlaying || Time.unscaledTime < nextStepTime)
            return;

        // VMDClient requests fresh coordinates after STEP succeeds. Wait for them
        // before advancing again so slow responses do not build a command queue.
        waitingForCoordinates = subscribedClient.SendCommand("STEP 1");
        if (!waitingForCoordinates)
        {
            isPlaying = false;
            return;
        }
        requestTime = Time.unscaledTime;
        nextStepTime = requestTime + stepInterval;
    }

    public void Play()
    {
        if (!isActiveAndEnabled || subscribedClient == null || !subscribedClient.IsConnected)
            return;

        isPlaying = true;
        nextStepTime = Time.unscaledTime;
    }

    public void Pause()
    {
        isPlaying = false;
    }

    private void OnCoordinatesReceived(IReadOnlyList<Vector3> coordinates)
    {
        waitingForCoordinates = false;
    }

    private void OnDisable()
    {
        if (subscribedClient != null)
            subscribedClient.CoordinatesReceived -= OnCoordinatesReceived;
        subscribedClient = null;
        isPlaying = false;
        waitingForCoordinates = false;
    }
}
