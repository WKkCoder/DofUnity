using System;
using System.Collections;
using System.IO;
using Dnf.Client;
using Dnf.Data;
using Dnf.Render;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 用户自备的声音数据能不能被 Unity 解码成 AudioClip(证据 E-089、E-090)。需要 DNF_DIR(没有就跳过);不需要图形设备。
    /// 走的是 <see cref="DnfAudio"/> 实际用的那条路:包里的条目写成临时文件再读(整个读进内存、保持压缩),散装的音乐直接从原路径读。
    /// 断言只看剪辑的时长、通道数、采样率这些物理量,不对任何原版声音的具体数值下结论。
    /// </summary>
    [Category("Audio")]
    public class AudioDecodeTests
    {
        private static string DataDir()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实声音数据的解码");
            }
            return dir;
        }

        private static IEnumerator Load(string path, Action<AudioClip> done)
        {
            using (UnityWebRequest request = DnfAudio.CreateClipRequest(path))
            {
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                {
                    yield return null;
                }
                Assert.AreEqual(UnityWebRequest.Result.Success, request.result, request.error);
                AudioClip clip = ((DownloadHandlerAudioClip)request.downloadHandler).audioClip;
                Assert.IsNotNull(clip, "解码得不到剪辑");
                done(clip);
            }
        }

        [UnityTest]
        public IEnumerator APackedSoundDecodesThroughATemporaryFile()
        {
            string dir = DataDir();
            using (SoundLibrary library = SoundLibrary.Open(dir))
            {
                Assume.That(library.HasCatalog && library.PackedCount > 0, "数据目录里没有 audio.xml 或 SoundPacks");
                Assert.IsTrue(library.Catalog.TryResolveFirst("SM_DIE", () => 0.0, out ResolvedSound sound), "audio.xml 里应当有主角死亡的语音");
                Assert.IsTrue(library.TryRead(sound.File, out byte[] bytes), "包里应当有这个文件");
                Assert.Greater(bytes.Length, 100);

                string temp = Path.Combine(Application.temporaryCachePath, "dnf-audio-test-" + Guid.NewGuid().ToString("N") + ".ogg");
                File.WriteAllBytes(temp, bytes);
                AudioClip clip = null;
                try
                {
                    IEnumerator load = Load(temp, c => clip = c);
                    while (load.MoveNext())
                    {
                        yield return null;
                    }
                }
                finally
                {
                    File.Delete(temp);
                }
                Assert.AreNotEqual(AudioDataLoadState.Failed, clip.loadState);
                Assert.Greater(clip.length, 0.05f, "时长");
                Assert.That(clip.channels, Is.InRange(1, 2), "通道数");
                Assert.GreaterOrEqual(clip.frequency, 8000, "采样率");
                UnityObjects.Destroy(clip);
            }
        }

        [UnityTest]
        public IEnumerator ALooseMusicFileLoadsFromItsOwnPath()
        {
            string dir = DataDir();
            using (SoundLibrary library = SoundLibrary.Open(dir))
            {
                Assume.That(library.LooseCount > 0, "数据目录里没有 Music 目录");
                string path = null;
                foreach (AudioEntry e in library.Catalog.Entries)
                {
                    if (e.Kind == AudioKind.Music && !string.IsNullOrEmpty(e.File) && library.TryGetLoosePath(e.File, out path))
                    {
                        break;
                    }
                    path = null;
                }
                Assume.That(path != null, "audio.xml 里的音乐在 Music 目录里一个都找不到");

                AudioClip clip = null;
                IEnumerator load = Load(path, c => clip = c);
                while (load.MoveNext())
                {
                    yield return null;
                }
                Assert.AreNotEqual(AudioDataLoadState.Failed, clip.loadState);
                Assert.Greater(clip.length, 1f, "音乐至少有一秒长");
                UnityObjects.Destroy(clip);
            }
        }
    }
}
