using System.Security.Cryptography;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// GCM 的 nonce 来源。同一密钥下 nonce 绝不能重复,所以生产代码一律用 <see cref="RandomNonceSource"/>;
    /// 这个接口存在的唯一目的是让测试注入固定 nonce 以得到可复现的密文。
    /// </summary>
    public interface INonceSource
    {
        /// <summary>返回一个新的 12 字节 nonce(每次调用返回新数组)。</summary>
        byte[] NextNonce();
    }

    /// <summary>用系统密码学随机数生成 96 位随机 nonce(认证请求一分钟最多十来次,随机 nonce 的碰撞概率可以忽略)。</summary>
    public sealed class RandomNonceSource : INonceSource
    {
        /// <summary>共享实例(线程安全)。</summary>
        public static readonly RandomNonceSource Default = new RandomNonceSource();

        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        /// <inheritdoc />
        public byte[] NextNonce()
        {
            byte[] nonce = new byte[GcmAes.NonceSize];
            lock (_rng)
            {
                _rng.GetBytes(nonce);
            }
            return nonce;
        }
    }
}
