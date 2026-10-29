using System;
using UnityEngine;

namespace BounceV5;

/// <summary>
/// Per-target squash &amp; stretch overlay. Driven in LateUpdate; the
/// "unapply previous frame, apply new frame" trick composes with the game's
/// own tweens. Bottom-anchored by default.
/// </summary>
public class BounceDriver : MonoBehaviour
{
    public BounceDriver(IntPtr ptr) : base(ptr) { }

    private Vector3 _prevMult = Vector3.one;
    private Vector3 _prevOffset = Vector3.zero;
    private bool _hasPrev;

    private Transform _t;
    private bool _anchorCached;
    private float _pivotY;
    private float _rectHeight;

    private int _id;
    private bool _idCached;
    private float _lastErrTime;

    private void LateUpdate()
    {
        try
        {
            LateUpdateInternal();
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime - _lastErrTime > 5f)
            {
                _lastErrTime = Time.unscaledTime;
                Plugin.Logger.LogError($"[BounceV5] driver failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void LateUpdateInternal()
    {
        if (!Plugin.Enabled.Value || IsUserExcluded())
        {
            RestoreOnce();
            return;
        }

        if (_t == null)
            _t = transform;

        float freq = V5Manager.CurrentFreq();
        float phase = Time.time * freq;
        float s = Mathf.Abs(Mathf.Sin(Mathf.PI * (phase - Mathf.Floor(phase)))); // 0 landing, 1 apex

        float squash = Mathf.Clamp(Plugin.SquashAmount.Value, 0f, 0.5f);
        float stretch = Mathf.Clamp(Plugin.StretchAmount.Value, 0f, 0.5f);

        float multY = (1f - squash) + (stretch + squash) * s;
        float multX = 2f - multY;
        float offsetY = Plugin.HopHeight.Value * s;

        if (Plugin.AnchorBottom.Value)
            offsetY += BottomAnchorCompensation(multY);

        Apply(multX, multY, offsetY);
    }

    private bool IsUserExcluded()
    {
        if (!_idCached)
        {
            _id = GetInstanceID();
            _idCached = true;
        }
        return TargetSystem.IsExcluded(_id);
    }

    private float BottomAnchorCompensation(float multY)
    {
        if (!_anchorCached)
        {
            _anchorCached = true;
            RectTransform rt = _t.TryCast<RectTransform>();
            if (rt != null)
            {
                _pivotY = rt.pivot.y;
                _rectHeight = rt.rect.height;
            }
        }
        return _pivotY * _rectHeight * (multY - 1f);
    }

    private void Apply(float multX, float multY, float offsetY)
    {
        Vector3 scale = _t.localScale;
        Vector3 pos = _t.localPosition;

        if (_hasPrev)
        {
            if (Mathf.Abs(_prevMult.x) > 0.001f) scale.x /= _prevMult.x;
            if (Mathf.Abs(_prevMult.y) > 0.001f) scale.y /= _prevMult.y;
            pos -= _prevOffset;
        }

        _t.localScale = new Vector3(scale.x * multX, scale.y * multY, scale.z);
        _t.localPosition = pos + new Vector3(0f, offsetY, 0f);

        _prevMult = new Vector3(multX, multY, 1f);
        _prevOffset = new Vector3(0f, offsetY, 0f);
        _hasPrev = true;
    }

    public void RestoreOnce()
    {
        if (!_hasPrev || _t == null) return;
        Vector3 scale = _t.localScale;
        if (Mathf.Abs(_prevMult.x) > 0.001f) scale.x /= _prevMult.x;
        if (Mathf.Abs(_prevMult.y) > 0.001f) scale.y /= _prevMult.y;
        _t.localScale = scale;
        _t.localPosition -= _prevOffset;
        _prevMult = Vector3.one;
        _prevOffset = Vector3.zero;
        _hasPrev = false;
    }

    private void OnDisable() => RestoreOnce();
    private void OnDestroy() => RestoreOnce();
}
