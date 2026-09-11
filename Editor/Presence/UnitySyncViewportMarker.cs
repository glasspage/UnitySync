using System;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    [ExecuteAlways]
    internal sealed class UnitySyncViewportMarker : MonoBehaviour
    {
        private Guid _playerId;
        private string _displayName;
        private Color _color;
        private Vector3 _pivot;
        private float _fieldOfView = 60f;
        private float _aspect = 1.777f;
        private bool _orthographic;
        private float _orthographicSize = 5f;

        internal string DisplayName => _displayName;

        internal void Initialize(Guid playerId, string displayName, Color color)
        {
            _playerId = playerId;
            _displayName = displayName;
            _color = color;
        }

        internal void Apply(UnitySyncViewportState viewport)
        {
            _displayName = viewport.DisplayName;
            _pivot = viewport.Pivot;
            _fieldOfView = viewport.FieldOfView;
            _aspect = Mathf.Clamp(viewport.Aspect, 0.1f, 10f);
            _orthographic = viewport.Orthographic;
            _orthographicSize = viewport.OrthographicSize;

            gameObject.name = _displayName + " (Viewport)";
            transform.SetPositionAndRotation(viewport.Position, viewport.Rotation.normalized);
        }

        private void OnDrawGizmos()
        {
            Color previousColor = Gizmos.color;
            Matrix4x4 previousMatrix = Gizmos.matrix;

            Gizmos.color = _color;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

            if (_orthographic)
            {
                float height = Mathf.Clamp(_orthographicSize * 0.12f, 0.15f, 1.5f);
                float width = height * _aspect;
                Gizmos.DrawWireCube(Vector3.forward * 0.08f, new Vector3(width, height, 0.16f));
            }
            else
            {
                Gizmos.DrawFrustum(Vector3.zero, Mathf.Clamp(_fieldOfView, 5f, 170f), 0.8f, 0.05f, _aspect);
            }

            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 1.15f);
            Gizmos.DrawWireSphere(Vector3.forward * 1.15f, 0.04f);

            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;

            Handles.color = new Color(_color.r, _color.g, _color.b, 0.55f);
            Handles.DrawDottedLine(transform.position, _pivot, 4f);
            Handles.color = _color;
            Handles.Label(transform.position + transform.up * 0.2f, _displayName);
        }
    }
}
