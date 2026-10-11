using System;
using System.IO;

namespace DiscUtils.Ufs
{
    // Parsed struct fs. Field offsets from FreeBSD sys/ufs/ffs/fs.h:
    // ncg@44 bsize@48 fsize@52 frag@56 bmask@72 fmask@76 bshift@80
    // fshift@84 fragshift@96 fsbtodb@100 sbsize@104 nindir@116 inopb@120
    // optim@128 cgsize@160 ipg@184 fpg@188 sblkno@8 cblkno@12 iblkno@16
    // dblkno@20 cgoffset@24 cgmask@28 maxsymlinklen@1320 magic@1372
    // (UFS1 size/time use the old_ compat fields @36/@32).
    public sealed class UfsSuperblock
    {
        public bool IsUfs2;
        public bool BigEndian;
        public int BlockSize;
        public int FragSize;
        public int FragsPerBlock;
        public uint NumCylGroups;
        public uint InodesPerGroup;
        public uint FragsPerGroup;
        public uint InodesPerBlock;
        public uint AddrsPerBlock;
        public uint Sblkno;
        public uint Cblkno;
        public uint Iblkno;
        public uint Dblkno;
        public uint CgOffset;
        public uint CgMask;
        public ulong Size;
        public uint MaxSymlinkLen;
        public int DinodeSize;
        public int AddrSize;

        private UfsSuperblock()
        {
        }

        public static UfsSuperblock Read(byte[] sb, bool bigEndian)
        {
            if (sb == null || sb.Length < UfsConstants.SuperblockLength)
            {
                throw new IOException("ufs superblock too short");
            }
            uint magic = UfsConstants.ReadU32(sb, UfsConstants.MagicOffset, bigEndian);
            bool isUfs2;
            if (magic == UfsConstants.Ufs2Magic)
            {
                isUfs2 = true;
            }
            else if (magic == UfsConstants.Ufs1Magic)
            {
                isUfs2 = false;
            }
            else
            {
                throw new IOException("ufs bad magic");
            }
            var fs = new UfsSuperblock();
            fs.IsUfs2 = isUfs2;
            fs.BigEndian = bigEndian;
            fs.BlockSize = (int)UfsConstants.ReadU32(sb, 48, bigEndian);
            fs.FragSize = (int)UfsConstants.ReadU32(sb, 52, bigEndian);
            fs.FragsPerBlock = (int)UfsConstants.ReadU32(sb, 56, bigEndian);
            fs.NumCylGroups = UfsConstants.ReadU32(sb, 44, bigEndian);
            fs.InodesPerBlock = UfsConstants.ReadU32(sb, 120, bigEndian);
            fs.AddrsPerBlock = UfsConstants.ReadU32(sb, 116, bigEndian);
            int bshift = (int)UfsConstants.ReadU32(sb, 80, bigEndian);
            int fshift = (int)UfsConstants.ReadU32(sb, 84, bigEndian);
            uint bmask = UfsConstants.ReadU32(sb, 72, bigEndian);
            uint fmask = UfsConstants.ReadU32(sb, 76, bigEndian);
            int fragshift = (int)UfsConstants.ReadU32(sb, 96, bigEndian);
            fs.Sblkno = UfsConstants.ReadU32(sb, 8, bigEndian);
            fs.Cblkno = UfsConstants.ReadU32(sb, 12, bigEndian);
            fs.Iblkno = UfsConstants.ReadU32(sb, 16, bigEndian);
            fs.Dblkno = UfsConstants.ReadU32(sb, 20, bigEndian);
            fs.CgOffset = UfsConstants.ReadU32(sb, 24, bigEndian);
            fs.CgMask = UfsConstants.ReadU32(sb, 28, bigEndian);
            fs.InodesPerGroup = UfsConstants.ReadU32(sb, 184, bigEndian);
            fs.FragsPerGroup = UfsConstants.ReadU32(sb, 188, bigEndian);
            uint cgsize = UfsConstants.ReadU32(sb, 160, bigEndian);
            uint optim = UfsConstants.ReadU32(sb, 128, bigEndian);
            fs.MaxSymlinkLen = UfsConstants.ReadU32(sb, 1320, bigEndian);
            if (isUfs2)
            {
                fs.Size = UfsConstants.ReadU64(sb, 1080, bigEndian);
            }
            else
            {
                fs.Size = UfsConstants.ReadU32(sb, 36, bigEndian);
            }

            if (fs.BlockSize != 4096 && fs.BlockSize != 8192 && fs.BlockSize != 16384
                && fs.BlockSize != 32768 && fs.BlockSize != 65536)
            {
                throw new IOException("ufs bad block size");
            }
            if (fs.FragSize < 512 || fs.FragSize > fs.BlockSize || fs.BlockSize % fs.FragSize != 0)
            {
                throw new IOException("ufs bad frag size");
            }
            if (fs.FragsPerBlock < 1 || fs.FragsPerBlock > 8 || fs.FragsPerBlock != fs.BlockSize / fs.FragSize)
            {
                throw new IOException("ufs bad frag count");
            }
            if (fs.NumCylGroups < 1 || fs.NumCylGroups > 200000)
            {
                throw new IOException("ufs bad group count");
            }
            uint wantInopb = (uint)(fs.BlockSize / (isUfs2 ? 256 : 128));
            if (fs.InodesPerBlock != wantInopb)
            {
                throw new IOException("ufs bad inodes-per-block");
            }
            uint wantNindir = (uint)(fs.BlockSize / (isUfs2 ? 8 : 4));
            if (fs.AddrsPerBlock != wantNindir)
            {
                throw new IOException("ufs bad addrs-per-block");
            }
            if (bshift < 0 || bshift > 31 || (1 << bshift) != fs.BlockSize)
            {
                throw new IOException("ufs bad block shift");
            }
            if (fshift < 0 || fshift > 31 || (1 << fshift) != fs.FragSize)
            {
                throw new IOException("ufs bad frag shift");
            }
            if (bmask != ~(uint)(fs.BlockSize - 1) || fmask != ~(uint)(fs.FragSize - 1))
            {
                throw new IOException("ufs bad masks");
            }
            if (fragshift < 0 || fragshift > 3 || (1 << fragshift) != fs.FragsPerBlock)
            {
                throw new IOException("ufs bad frag shift");
            }
            if (optim > 1)
            {
                throw new IOException("ufs bad optim");
            }
            if (cgsize < 64 || cgsize > (uint)fs.BlockSize)
            {
                throw new IOException("ufs bad cg size");
            }
            if (fs.InodesPerGroup < 1 || fs.InodesPerGroup > 1048576)
            {
                throw new IOException("ufs bad inodes-per-group");
            }
            if (fs.FragsPerGroup < 1 || fs.FragsPerGroup > 0x10000000)
            {
                throw new IOException("ufs bad frags-per-group");
            }
            if (fs.Size == 0 || fs.Size > (ulong)fs.NumCylGroups * fs.FragsPerGroup + fs.FragsPerGroup)
            {
                throw new IOException("ufs bad size");
            }
            if (fs.MaxSymlinkLen < 1 || fs.MaxSymlinkLen > 512)
            {
                throw new IOException("ufs bad max symlink length");
            }
            ulong time = isUfs2
                ? UfsConstants.ReadU64(sb, 1072, bigEndian)
                : UfsConstants.ReadU32(sb, 32, bigEndian);
            if (time < 315532800UL || time > 4102444800UL)
            {
                throw new IOException("ufs bad time");
            }
            fs.DinodeSize = isUfs2 ? 256 : 128;
            fs.AddrSize = isUfs2 ? 8 : 4;
            return fs;
        }

        // First frag (fragment number) of cylinder group cg.
        public ulong CgStart(uint cg)
        {
            ulong start = (ulong)FragsPerGroup * cg;
            if (!IsUfs2 && CgMask != 0)
            {
                start += (ulong)CgOffset * (cg & ~CgMask);
            }
            return start;
        }

        // Frag number holding inode ino.
        public ulong InodeFrag(uint ino)
        {
            uint cg = ino / InodesPerGroup;
            ulong groupStart = CgStart(cg) + Iblkno;
            uint indexInGroup = ino % InodesPerGroup;
            return groupStart + (indexInGroup / InodesPerBlock) * (uint)(BlockSize / FragSize);
        }

        public long FragBytes(ulong frag)
        {
            return (long)(frag * (ulong)FragSize);
        }
    }
}
