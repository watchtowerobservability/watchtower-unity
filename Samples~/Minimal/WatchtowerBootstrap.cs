using UnityEngine;
using Watchtower;

public class WatchtowerBootstrap : MonoBehaviour
{
    void Awake()
    {
        WatchtowerSdk.Init(new WatchtowerOptions
        {
            Key = "<publishable-key>",
            Endpoint = "https://<your-edge>",
            Service = "my-game",
        });
        Debug.LogError("watchtower test error");
        WatchtowerSdk.CaptureException(new System.InvalidOperationException("watchtower test exception"));
    }
}
