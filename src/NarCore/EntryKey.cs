using System.Text;

namespace NarTool.Core;

/// <summary>
/// 由条目路径派生 16 字节异或密钥。算法与原工具一致：
/// 先做 Python 风格的乘异或哈希，再用 LCG 逐字节扩展。
/// </summary>
public static class EntryKey
{
    public static uint PythonHash(byte[] data)
    {
        uint h = 0;
        for (int i = 0; i < data.Length; i++)
            h = (h * 1000003u) ^ data[i];
        return h ^ (uint)data.Length;
    }

    public static byte[] Create(byte[] path)
    {
        uint h = PythonHash(path);
        byte[] key = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            h = h * 1103515245u + 12345u;
            key[i] = (byte)(h & 0xFF);
        }
        return key;
    }

    public static byte[] Create(string path) => Create(Encoding.UTF8.GetBytes(path));
}
