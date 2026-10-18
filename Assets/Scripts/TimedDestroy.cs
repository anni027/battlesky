using UnityEngine;

/// <summary>
/// Destroys its GameObject after a fixed lifetime. Used by local-only
/// cosmetics such as bolt impact flashes, which are spawned on every client
/// by a ClientRpc and must clean themselves up locally.
/// </summary>
public class TimedDestroy : MonoBehaviour
{
    [SerializeField] private float _lifetime = 0.25f;

    private void Start()
    {
        Destroy(gameObject, _lifetime);
    }
}
