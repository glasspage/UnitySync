using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync.Tests
{
    public sealed class UnitySyncRectTransformReconciliationTests
    {
        private const BindingFlags StaticMethods = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type Serializer = Type.GetType(
            "Glasspage.UnitySync.UnitySyncSceneSerializer, Glasspage.UnitySync.Editor", true);

        [Test]
        public void ReconcileComponentsConvertsTransformToRectTransform()
        {
            GameObject source = new GameObject(
                "Canvas source",
                typeof(RectTransform),
                typeof(Canvas));
            GameObject target = new GameObject("Canvas target");

            try
            {
                object[] captureArguments = { source, false, null };
                MethodInfo captureHierarchy = Serializer.GetMethod("TryCaptureHierarchy", StaticMethods);
                Assert.That(captureHierarchy, Is.Not.Null);
                Assert.That(
                    (bool)captureHierarchy.Invoke(null, captureArguments),
                    Is.True);

                object change = captureArguments[2];
                Assert.That(change, Is.Not.Null);
                FieldInfo componentsField = change.GetType().GetField("Components", InstanceFields);
                Assert.That(componentsField, Is.Not.Null);
                object desiredStates = componentsField.GetValue(change);

                object[] reconcileArguments = { target, desiredStates, null };
                MethodInfo reconcileComponents = Serializer.GetMethod("ReconcileComponents", StaticMethods);
                Assert.That(reconcileComponents, Is.Not.Null);
                Assert.That(
                    (bool)reconcileComponents.Invoke(null, reconcileArguments),
                    Is.True,
                    reconcileArguments[2] as string);

                Assert.That(target.transform, Is.InstanceOf<RectTransform>());
                Assert.That(target.GetComponent<Canvas>(), Is.Not.Null);
                Assert.That(
                    target.GetComponents<Component>().Length,
                    Is.EqualTo(source.GetComponents<Component>().Length));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
            }
        }
    }
}
