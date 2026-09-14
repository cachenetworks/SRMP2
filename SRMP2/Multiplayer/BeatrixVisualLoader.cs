using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace SRMP2.Multiplayer;

internal sealed class BeatrixVisualLoader
{
    private const string RigAddress = "beatrix_SR2_rig.fbx";
    private const string IdleControllerAddress = "BeatrixIdle.controller";

    private readonly Action<string> _log;
    private readonly BeatrixVisualLoadState _state = new();

    private AsyncOperationHandle<GameObject> _rigHandle;
    private AsyncOperationHandle<RuntimeAnimatorController> _idleControllerHandle;
    private bool _hasRigHandle;
    private bool _hasIdleControllerHandle;
    private GameObject _rigAsset;
    private RuntimeAnimatorController _idleController;

    internal BeatrixVisualLoader(Action<string> logger)
    {
        _log = logger ?? (_ => { });
    }

    internal BeatrixVisualLoadPhase Phase => _state.Phase;

    internal void RequestLoad()
    {
        if (!_state.TryBeginLoad())
            return;

        try
        {
            _rigHandle = Addressables.LoadAssetAsync<GameObject>(RigAddress);
            _hasRigHandle = true;
            _idleControllerHandle = Addressables.LoadAssetAsync<RuntimeAnimatorController>(IdleControllerAddress);
            _hasIdleControllerHandle = true;
            _log("Loading shared Beatrix remote-player visual assets.");
        }
        catch (Exception ex)
        {
            ReleaseHandles();
            _state.MarkFailed();
            _log($"Beatrix visual assets unavailable; keeping capsule fallback: {ex.Message}");
        }
    }

    internal void Update()
    {
        if (_state.Phase != BeatrixVisualLoadPhase.Loading)
            return;
        if (!_hasRigHandle || !_hasIdleControllerHandle)
            return;
        if (!_rigHandle.IsDone || !_idleControllerHandle.IsDone)
            return;

        if (_rigHandle.Status != AsyncOperationStatus.Succeeded
            || _idleControllerHandle.Status != AsyncOperationStatus.Succeeded
            || _rigHandle.Result == null
            || _idleControllerHandle.Result == null)
        {
            var rigStatus = _rigHandle.Status;
            var animatorStatus = _idleControllerHandle.Status;
            ReleaseHandles();
            _state.MarkFailed();
            _log($"Beatrix visual assets failed to load; keeping capsule fallback (rig={rigStatus}, idle={animatorStatus}).");
            return;
        }

        _rigAsset = _rigHandle.Result;
        _idleController = _idleControllerHandle.Result;
        _state.MarkReady();
        _log("Shared Beatrix remote-player visual assets loaded.");
    }

    internal bool TryCreateVisual(Transform parent, out GameObject visual, out string reason)
    {
        visual = null;
        reason = string.Empty;

        if (_state.Phase != BeatrixVisualLoadPhase.Ready || _rigAsset == null || _idleController == null)
        {
            reason = "shared Beatrix assets are not ready";
            return false;
        }

        GameObject clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(_rigAsset);
            clone.name = "BeatrixVisual";
            clone.transform.SetParent(parent, worldPositionStays: false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;

            if (clone.GetComponentInChildren<Camera>(includeInactive: true) != null)
            {
                reason = "loaded rig unexpectedly contains a Camera";
                UnityEngine.Object.Destroy(clone);
                return false;
            }

            var animator = clone.GetComponentInChildren<Animator>(includeInactive: true);
            if (animator == null)
            {
                reason = "loaded rig has no Animator";
                UnityEngine.Object.Destroy(clone);
                return false;
            }

            var renderers = clone.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers == null || renderers.Length == 0)
            {
                reason = "loaded rig has no renderers";
                UnityEngine.Object.Destroy(clone);
                return false;
            }

            animator.applyRootMotion = false;
            animator.runtimeAnimatorController = _idleController;

            var colliders = clone.GetComponentsInChildren<Collider>(includeInactive: true);
            if (colliders != null)
            {
                foreach (var collider in colliders)
                {
                    if (collider != null)
                        collider.enabled = false;
                }
            }

            visual = clone;
            return true;
        }
        catch (Exception ex)
        {
            if (clone != null)
                UnityEngine.Object.Destroy(clone);
            reason = ex.Message;
            return false;
        }
    }

    internal void Reset()
    {
        var hadHandles = _hasRigHandle || _hasIdleControllerHandle;
        ReleaseHandles();
        _rigAsset = null;
        _idleController = null;
        _state.Reset();

        if (hadHandles)
            _log("Released shared Beatrix remote-player visual assets.");
    }

    private void ReleaseHandles()
    {
        if (_hasRigHandle)
        {
            try
            {
                Addressables.Release(_rigHandle);
            }
            catch
            {
                // Addressables cleanup should never disturb multiplayer teardown.
            }
            _hasRigHandle = false;
        }

        if (_hasIdleControllerHandle)
        {
            try
            {
                Addressables.Release(_idleControllerHandle);
            }
            catch
            {
                // Addressables cleanup should never disturb multiplayer teardown.
            }
            _hasIdleControllerHandle = false;
        }
    }
}
