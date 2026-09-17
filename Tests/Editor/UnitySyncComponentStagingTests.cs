using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync.Tests
{
    public sealed class UnitySyncComponentStagingTests
    {
        private static readonly Type Serializer = Type.GetType(
            "Glasspage.UnitySync.UnitySyncSceneSerializer, Glasspage.UnitySync.Editor", true);

        private static IEnumerable AudioFilterCases()
        {
            Type[] filters =
            {
                typeof(AudioLowPassFilter), typeof(AudioHighPassFilter),
                typeof(AudioEchoFilter), typeof(AudioDistortionFilter),
                typeof(AudioReverbFilter), typeof(AudioChorusFilter)
            };
            string[] properties =
            {
                "cutoffFrequency", "cutoffFrequency", "delay", "distortionLevel",
                "dryLevel", "dryMix"
            };
            float[] values = { 1234f, 2345f, 321f, 0.42f, -1234f, 0.37f };
            foreach (Type owner in new[] { typeof(AudioSource), typeof(AudioListener) })
            {
                for (int index = 0; index < filters.Length; index++)
                {
                    yield return new TestCaseData(filters[index], owner, properties[index], values[index]);
                }
            }
        }

        [TestCaseSource(nameof(AudioFilterCases))]
        public void AudioFilterSettingsApplyThroughStaging(
            Type filterType, Type ownerType, string propertyName, float incomingValue)
        {
            GameObject target = new GameObject("Audio filter staging test");
            target.SetActive(false);
            try
            {
                target.AddComponent(ownerType);
                Component filter = target.AddComponent(filterType);
                Assert.That(filter, Is.Not.Null);
                PropertyInfo property = filterType.GetProperty(propertyName);
                property.SetValue(filter, incomingValue);
                if (filter is AudioReverbFilter reverb)
                {
                    reverb.reverbPreset = AudioReverbPreset.User;
                    property.SetValue(filter, incomingValue);
                }

                object[] capture = { filter, 2, null };
                Assert.That(Invoke("TryCaptureComponentState", capture), Is.True);
                property.SetValue(filter, incomingValue + 0.1f);
                int componentCount = target.GetComponents<Component>().Length;

                object[] apply = { filter, capture[2], null };
                Assert.That(Invoke("ApplyComponentThroughStaging", apply), Is.True, apply[2] as string);
                Assert.That((float)property.GetValue(filter), Is.EqualTo(incomingValue).Within(0.01f));
                Assert.That(target.GetComponents<Component>().Length, Is.EqualTo(componentCount));
                Assert.That(target.GetComponent(ownerType), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ParticleSystemRendererStagingCreatesItsOwner()
        {
            GameObject source = new GameObject("Particle staging test");
            source.SetActive(false);
            object[] arguments = { null, null, null };
            try
            {
                source.AddComponent<ParticleSystem>();
                arguments[0] = source.GetComponent<ParticleSystemRenderer>();
                Assert.That(Invoke("TryCreateStagingComponent", arguments), Is.True);
                GameObject staging = (GameObject)arguments[1];
                Assert.That(staging.activeSelf, Is.False);
                Assert.That(staging.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(staging.GetComponent<ParticleSystem>(), Is.Not.Null);
                Assert.That(arguments[2], Is.InstanceOf<ParticleSystemRenderer>());
                Assert.That(staging.GetComponents<ParticleSystemRenderer>().Length, Is.EqualTo(1));
            }
            finally
            {
                if (arguments[1] is GameObject staging)
                {
                    Object.DestroyImmediate(staging);
                }
                Object.DestroyImmediate(source);
            }
        }

        private static bool Invoke(string method, object[] arguments)
        {
            return (bool)Serializer.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, arguments);
        }
    }
}
