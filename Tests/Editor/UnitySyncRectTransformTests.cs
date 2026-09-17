using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync.Tests
{
    public sealed class UnitySyncRectTransformTests
    {
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type Serializer = Type.GetType(
            "Glasspage.UnitySync.UnitySyncSceneSerializer, Glasspage.UnitySync.Editor", true);
        private static readonly Type Registry = Type.GetType(
            "Glasspage.UnitySync.UnitySyncSceneObjectRegistry, Glasspage.UnitySync.Editor", true);

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ExistingObjectUpgradesForHierarchyAndFullUpdates(bool hierarchyOnly, bool canvas)
        {
            GameObject parent = new GameObject("RectTransform test parent");
            GameObject source = new GameObject("Incoming UI", typeof(RectTransform));
            GameObject target = new GameObject("Already synchronized object");
            GameObject child = new GameObject("Existing child");
            try
            {
                source.SetActive(false);
                target.SetActive(false);
                source.transform.SetParent(parent.transform, false);
                target.transform.SetParent(parent.transform, false);
                child.transform.SetParent(target.transform, false);
                source.AddComponent<BoxCollider>();
                BoxCollider retainedComponent = target.AddComponent<BoxCollider>();
                if (canvas)
                {
                    source.AddComponent<Canvas>();
                }

                RectTransform incoming = (RectTransform)source.transform;
                incoming.anchorMin = new Vector2(0.2f, 0.3f);
                incoming.anchorMax = new Vector2(0.7f, 0.8f);
                incoming.pivot = new Vector2(0.4f, 0.6f);
                incoming.sizeDelta = new Vector2(123f, 234f);
                incoming.anchoredPosition3D = new Vector3(12f, 34f, 5f);
                incoming.localRotation = Quaternion.Euler(0f, 0f, 15f);
                incoming.localScale = new Vector3(2f, 3f, 1f);

                object change = Capture(source, hierarchyOnly);
                object fullChange = Capture(source, false);
                object address = Field(change, "Address");
                string objectId = (string)Field(address, "ObjectId");
                Registry.GetMethod("Assign", Static).Invoke(null, new object[] { target, objectId });
                int instanceId = target.GetInstanceID();

                Apply(change);
                RectTransform received = target.transform as RectTransform;
                Assert.That(received, Is.Not.Null);
                Assert.That(target.GetInstanceID(), Is.EqualTo(instanceId));
                Assert.That(target.transform.parent, Is.EqualTo(parent.transform));
                Assert.That(child.transform.parent, Is.EqualTo(received));
                Assert.That(target.GetComponent<BoxCollider>(), Is.SameAs(retainedComponent));
                Assert.That(target.GetComponents<Component>().Length, Is.EqualTo(canvas ? 3 : 2));
                if (canvas)
                {
                    Assert.That(target.GetComponent<Canvas>(), Is.Not.Null);
                }
                object[] resolve = { objectId, null };
                Assert.That(Registry.GetMethod("TryResolve", Static).Invoke(null, resolve), Is.True);
                Assert.That(resolve[1], Is.SameAs(target));

                // Hierarchy packets establish the type; the following full packet carries layout.
                Apply(fullChange);
                Assert.That(received.anchorMin, Is.EqualTo(incoming.anchorMin));
                Assert.That(received.anchorMax, Is.EqualTo(incoming.anchorMax));
                Assert.That(received.pivot, Is.EqualTo(incoming.pivot));
                Assert.That(received.sizeDelta, Is.EqualTo(incoming.sizeDelta));
                Assert.That(Vector3.Distance(received.anchoredPosition3D, incoming.anchoredPosition3D),
                    Is.LessThan(0.001f));
                Assert.That(Quaternion.Angle(received.localRotation, incoming.localRotation),
                    Is.LessThan(0.001f));
                Assert.That(received.localScale, Is.EqualTo(incoming.localScale));

                Apply(change);
                Assert.That(target.transform, Is.SameAs(received));
                Assert.That(target.GetComponents<Component>().Length, Is.EqualTo(canvas ? 3 : 2));
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Registry.GetMethod("Clear", Static).Invoke(null, null);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NewObjectsStartWithRectTransform(bool canvas)
        {
            GameObject source = new GameObject("New UI", typeof(RectTransform));
            GameObject received = null;
            try
            {
                source.SetActive(false);
                if (canvas)
                {
                    source.AddComponent<Canvas>();
                }
                object change = Capture(source, true);
                string objectId = (string)Field(Field(change, "Address"), "ObjectId");
                Object.DestroyImmediate(source);

                Apply(change);
                object[] resolve = { objectId, null };
                Assert.That(Registry.GetMethod("TryResolve", Static).Invoke(null, resolve), Is.True);
                received = (GameObject)resolve[1];
                Assert.That(received.transform, Is.InstanceOf<RectTransform>());
                Assert.That(received.GetComponents<Component>().Length, Is.EqualTo(canvas ? 2 : 1));
            }
            finally
            {
                if (source != null) Object.DestroyImmediate(source);
                if (received != null) Object.DestroyImmediate(received);
                Registry.GetMethod("Clear", Static).Invoke(null, null);
            }
        }

        private static object Capture(GameObject source, bool hierarchyOnly)
        {
            object[] arguments = hierarchyOnly
                ? new object[] { source, false, null }
                : new object[] { source, null };
            string method = hierarchyOnly ? "TryCaptureHierarchy" : "TryCaptureFullObject";
            Assert.That(Serializer.GetMethod(method, Static).Invoke(null, arguments), Is.True);
            return arguments[arguments.Length - 1];
        }

        private static void Apply(object change)
        {
            object[] arguments = { change, null };
            Assert.That(Serializer.GetMethod("Apply", Static).Invoke(null, arguments),
                Is.True, arguments[1] as string);
        }

        private static object Field(object value, string name)
        {
            return value.GetType().GetField(name, Instance).GetValue(value);
        }
    }
}
