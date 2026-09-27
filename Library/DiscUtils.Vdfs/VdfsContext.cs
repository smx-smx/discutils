using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using DiscUtils.Compression;
using DiscUtils.Vfs;

namespace DiscUtils.Vdfs
{
    /// <summary>
    /// One catalog-tree record: the (parent, name) key plus the raw value
    /// bytes (folder record, or folder record + data fork for files).
    /// </summary>
    public sealed class VdfsCatRecord    {
        public ulong ParentId;
        public ulong ObjectId;
        public byte Type;
        public string Name = string.Empty;
        public byte[] Value = new byte[0];
    }

    public struct VdfsExtent
    {
        public ulong Begin;
        public ulong Length;

        public bool IsEmpty
        {
            get { return Begin == 0 && Length == 0; }
        }
    }

    public struct VdfsOverflowExtent
    {
        public ulong IBlock;
        public VdfsExtent Extent;
    }

    /// <summary>
    /// Read-only VDFS engine: volume/snapshot parsing, metadata-block
    /// mapping, catalog b-tree walks and file-data reassembly (inline,
    /// extent, zlib/gzip-chunked and symlink targets). Hash/signature
    /// checking is intentionally skipped (read-only, verify-less open).
    /// </summary>
    public sealed class VdfsContext : VfsContext
    {
        private readonly Stream _stream;
        private readonly Dictionary<ulong, VdfsBtreeHead> _trees = new Dictionary<ulong, VdfsBtreeHead>();
        private Dictionary<ulong, VdfsCatRecord> _byObject;
        private Dictionary<ulong, List<VdfsOverflowExtent>> _overflow;

        public int BlockSize;
        public int SuperPageSize;
        public int BlocksPerPage;
        public int TreePageShift;
        public ulong VolumeBlocks;
        public string VolumeName = string.Empty;

        public VdfsExtent Tables;
        public readonly VdfsExtent[] Meta = new VdfsExtent[VdfsConstants.MetaBtreeExtents];
        public readonly ulong[] LastPageIndex = new ulong[VdfsConstants.SfCount];
        public readonly ulong[] TableOffsets = new ulong[VdfsConstants.SfCount];

        public ulong CatTreeIno = VdfsConstants.CatTreeIno;
        public ulong ExtTreeIno = VdfsConstants.ExtTreeIno;

        private VdfsContext(Stream stream)
        {
            _stream = stream;
        }

        public static VdfsContext Open(Stream stream)
        {
            if (stream == null || !stream.CanSeek)
            {
                throw new IOException("VDFS needs a seekable stream");
            }
            if (stream.Length < VdfsConstants.SectorSize * 8)
            {
                throw new IOException("image too small for VDFS superblock");
            }

            byte[] vb = VdfsConstants.ReadBytes(stream, VdfsConstants.VbOffset, 8);
            if (vb[0] != 'V' || vb[1] != 'D' || vb[2] != 'F' || vb[3] != 'S')
            {
                throw new IOException("not a VDFS volume");
            }
            string layout = Encoding.ASCII.GetString(vb, 4, 4);
            if (layout != VdfsConstants.Layout2007 && layout != VdfsConstants.Layout2006)
            {
                throw new IOException("unsupported VDFS layout: " + layout);
            }

            byte[] sb = VdfsConstants.ReadBytes(stream, VdfsConstants.SbOffset, VdfsConstants.SectorSize);
            if (sb[0] != 'V' || sb[1] != 'D' || sb[2] != 'F' || sb[3] != 'S')
            {
                throw new IOException("bad VDFS superblock signature");
            }
            int logBlock = sb[VdfsConstants.SbLogBlockSizeOff];
            int logPage = sb[VdfsConstants.SbLogSuperPageOff];
            if (logBlock < 9 || logBlock > 12 || logPage < logBlock || logPage > 20)
            {
                throw new IOException("bad VDFS block/page geometry");
            }

            var ctx = new VdfsContext(stream);
            ctx.BlockSize = 1 << logBlock;
            ctx.SuperPageSize = 1 << logPage;
            ctx.BlocksPerPage = 1 << (logPage - logBlock);
            ctx.TreePageShift = (logPage - logBlock) + logBlock - 12;
            ctx.VolumeName = Encoding.ASCII.GetString(
                sb, VdfsConstants.SbVolumeNameOff, VdfsConstants.SbVolumeNameLen).TrimEnd('\0');

            byte[] exsb = VdfsConstants.ReadBytes(
                stream, VdfsConstants.ExsbOffset, VdfsConstants.SectorSize * VdfsConstants.ExsbSectors);
            ctx.Tables = new VdfsExtent
            {
                Begin = VdfsConstants.ReadU64(exsb, VdfsConstants.ExsbTablesOff),
                Length = VdfsConstants.ReadU64(exsb, VdfsConstants.ExsbTablesOff + 8)
            };
            if (ctx.Tables.IsEmpty)
            {
                throw new IOException("VDFS translation-tables extent is empty");
            }
            for (int i = 0; i < VdfsConstants.MetaBtreeExtents; i++)
            {
                ctx.Meta[i] = new VdfsExtent
                {
                    Begin = VdfsConstants.ReadU64(exsb, VdfsConstants.ExsbMetaOff + i * 16),
                    Length = VdfsConstants.ReadU64(exsb, VdfsConstants.ExsbMetaOff + i * 16 + 8)
                };
            }
            ctx.VolumeBlocks = VdfsConstants.ReadU64(exsb, VdfsConstants.ExsbVolumeBlocksOff);

            long tablesBase = (long)(ctx.Tables.Begin * (ulong)ctx.BlockSize);
            byte[] bt = VdfsConstants.ReadBytes(stream, tablesBase, 24 + VdfsConstants.SfCount * 16);
            if (bt[0] != 'C' || bt[1] != 'o' || bt[2] != 'W' || bt[3] != 'B')
            {
                throw new IOException("bad VDFS snapshot base table");
            }
            for (int i = 0; i < VdfsConstants.SfCount; i++)
            {
                ctx.LastPageIndex[i] = VdfsConstants.ReadU64(bt, VdfsConstants.BaseTableLastPageOff + i * 8);
                ctx.TableOffsets[i] = VdfsConstants.ReadU64(bt, VdfsConstants.BaseTableOffsetsOff + i * 8);
            }
            ctx._tablesBase = tablesBase;

            // Current images catalog at ino 3 (older layouts used 2);
            // accept whichever page 0 presents a b-tree head at.
            if (!ctx.TreeHead(ctx.CatTreeIno, true))
            {
                ctx.CatTreeIno = VdfsConstants.CatTreeInoLegacy;
                if (!ctx.TreeHead(ctx.CatTreeIno, true))
                {
                    throw new IOException("VDFS catalog tree not found");
                }
            }
            if (!ctx.TreeHead(ctx.ExtTreeIno, true))
            {
                ctx.ExtTreeIno = VdfsConstants.ExtTreeInoLegacy;
                ctx.TreeHead(ctx.ExtTreeIno, true);
            }
            return ctx;
        }

        private long _tablesBase;

        private bool TreeHead(ulong ino, bool probe)
        {
            try
            {
                byte[] page = ReadMetaPage(ino, 0);
                if (page[0] != 'e' || page[1] != 'H' || page[2] != 'N' || page[3] != 'D')
                {
                    return false;
                }
                _trees[ino] = new VdfsBtreeHead
                {
                    Root = VdfsConstants.ReadU32(page, 12),
                    Height = VdfsConstants.ReadU16(page, 16)
                };
                return true;
            }
            catch (IOException)
            {
                if (probe)
                {
                    return false;
                }
                throw;
            }
        }

        internal VdfsBtreeHead GetTreeHead(ulong ino)
        {
            VdfsBtreeHead head;
            if (!_trees.TryGetValue(ino, out head))
            {
                if (!TreeHead(ino, false))
                {
                    throw new IOException("VDFS tree missing for ino " + ino);
                }
                head = _trees[ino];
            }
            return head;
        }

        private static int SfIndex(ulong ino)
        {
            // Special files run CAT(3)..XATTR(7); legacy layouts 2..6.
            long idx = (long)ino - (long)VdfsConstants.CatTreeIno;
            if (idx < 0 || idx >= VdfsConstants.SfCount)
            {
                idx = (long)ino - (long)VdfsConstants.CatTreeInoLegacy;
            }
            return (int)idx;
        }

        public byte[] ReadBlocks(ulong begin, ulong count)
        {
            ulong bytes = checked(begin * (ulong)BlockSize);
            ulong len = checked(count * (ulong)BlockSize);
            if (bytes + len > (ulong)_stream.Length)
            {
                throw new IOException("VDFS extent out of range");
            }
            return VdfsConstants.ReadBytes(_stream, (long)bytes, (int)len);
        }

        private ulong MetaIBlock(ulong ino, ulong page)
        {
            int sf = SfIndex(ino);
            if (sf < 0 || sf >= VdfsConstants.SfCount)
            {
                throw new IOException("VDFS invalid special ino " + ino);
            }
            bool isTree = ino >= VdfsConstants.CatTreeInoLegacy && ino <= VdfsConstants.CatTreeInoLegacy + 10;
            ulong tablePage = isTree ? (page >> TreePageShift) : page;
            if (tablePage > LastPageIndex[sf])
            {
                throw new IOException("VDFS page out of range");
            }
            long recOff = _tablesBase + (long)TableOffsets[sf] + (long)(tablePage * VdfsConstants.BaseTableRecordSize);
            byte[] rec = VdfsConstants.ReadBytes(_stream, recOff, VdfsConstants.BaseTableRecordSize);
            return VdfsConstants.ReadU64(rec, 0);
        }

        private ulong MetaToBlock(ulong metaIBlock)
        {
            ulong seen = 0;
            for (int i = 0; i < Meta.Length; i++)
            {
                if (Meta[i].Length == 0)
                {
                    continue;
                }
                if (metaIBlock < seen + Meta[i].Length)
                {
                    return Meta[i].Begin + (metaIBlock - seen);
                }
                seen += Meta[i].Length;
            }
            throw new IOException("VDFS meta block unmapped");
        }

        public byte[] ReadMetaPage(ulong ino, ulong page)
        {
            ulong metaIBlock = MetaIBlock(ino, page);
            ulong block = MetaToBlock(metaIBlock);
            return ReadBlocks(block, (ulong)BlocksPerPage);
        }

        public byte[] ReadNode(ulong ino, uint nodeId)
        {
            if (nodeId == 0)
            {
                return ReadMetaPage(ino, 0);
            }
            ulong pagesPerNode = (ulong)BlocksPerPage * (ulong)BlockSize / 4096;
            if (pagesPerNode == 0)
            {
                pagesPerNode = 1;
            }
            return ReadMetaPage(ino, (ulong)nodeId * pagesPerNode);
        }

        internal Dictionary<ulong, VdfsCatRecord> RecordsByObject()
        {
            if (_byObject == null)
            {
                _byObject = new Dictionary<ulong, VdfsCatRecord>();
                foreach (byte[] raw in new VdfsBtree(this, CatTreeIno).AllLeafRecords())
                {
                    VdfsCatRecord rec = VdfsBtree.ParseRecord(raw);
                    if (rec == null)
                    {
                        continue;
                    }
                    if (rec.Type == VdfsConstants.RecFile || rec.Type == VdfsConstants.RecFolder)
                    {
                        _byObject[rec.ObjectId] = rec;
                    }
                }
            }
            return _byObject;
        }

        internal VdfsCatRecord LookupObject(ulong objectId)
        {
            VdfsCatRecord rec;
            if (RecordsByObject().TryGetValue(objectId, out rec))
            {
                return rec;
            }
            return null;
        }

        private Dictionary<ulong, List<VdfsOverflowExtent>> OverflowMap()
        {
            if (_overflow == null)
            {
                _overflow = new Dictionary<ulong, List<VdfsOverflowExtent>>();
                try
                {
                    foreach (byte[] raw in new VdfsBtree(this, ExtTreeIno).AllLeafRecords())
                    {
                        if (raw.Length < 40)
                        {
                            continue;
                        }
                        ulong oid = VdfsConstants.ReadU64(raw, 8);
                        var entry = new VdfsOverflowExtent
                        {
                            IBlock = VdfsConstants.ReadU64(raw, 16),
                            Extent = new VdfsExtent
                            {
                                Begin = VdfsConstants.ReadU64(raw, 24),
                                Length = VdfsConstants.ReadU64(raw, 32)
                            }
                        };
                        if (entry.Extent.Length == 0)
                        {
                            continue;
                        }
                        List<VdfsOverflowExtent> list;
                        if (!_overflow.TryGetValue(oid, out list))
                        {
                            list = new List<VdfsOverflowExtent>();
                            _overflow[oid] = list;
                        }
                        list.Add(entry);
                    }
                    foreach (List<VdfsOverflowExtent> list in _overflow.Values)
                    {
                        list.Sort(delegate(VdfsOverflowExtent a, VdfsOverflowExtent b)
                        {
                            return a.IBlock.CompareTo(b.IBlock);
                        });
                    }
                }
                catch (IOException)
                {
                    // No extents tree (small images): fork extents suffice.
                }
            }
            return _overflow;
        }

        internal List<VdfsExtent> ResolveForRead(ulong objectId, ulong totalBlocks, byte[] fork)
        {
            return ResolveExtents(objectId, totalBlocks, totalBlocks, fork);
        }

        private List<VdfsExtent> ResolveExtents(ulong objectId, ulong fileSize, ulong totalBlocks, byte[] fork)
        {
            var result = new List<VdfsExtent>();
            ulong inlineEnd = 0;
            var inlineBlocks = new List<VdfsOverflowExtent>();
            for (int i = 0; i < VdfsConstants.ExtentsInFork; i++)
            {
                int off = VdfsConstants.ForkDataOff + i * VdfsConstants.IExtentSize;
                var extent = new VdfsExtent
                {
                    Begin = VdfsConstants.ReadU64(fork, off),
                    Length = VdfsConstants.ReadU64(fork, off + 8)
                };
                ulong iblock = VdfsConstants.ReadU64(fork, off + 16);
                if (extent.Length == 0)
                {
                    continue;
                }
                inlineBlocks.Add(new VdfsOverflowExtent { IBlock = iblock, Extent = extent });
                if (iblock + extent.Length > inlineEnd)
                {
                    inlineEnd = iblock + extent.Length;
                }
            }
            inlineBlocks.Sort(delegate(VdfsOverflowExtent a, VdfsOverflowExtent b)
            {
                return a.IBlock.CompareTo(b.IBlock);
            });
            ulong covered = 0;
            foreach (VdfsOverflowExtent e in inlineBlocks)
            {
                result.Add(e.Extent);
                covered += e.Extent.Length;
            }
            if (totalBlocks > covered && fileSize > 0)
            {
                List<VdfsOverflowExtent> extra;
                if (OverflowMap().TryGetValue(objectId, out extra))
                {
                    foreach (VdfsOverflowExtent e in extra)
                    {
                        if (e.IBlock >= inlineEnd)
                        {
                            result.Add(e.Extent);
                        }
                    }
                }
            }
            return result;
        }

        private byte[] ReadConcat(List<VdfsExtent> extents, ulong offset, ulong length)
        {
            var out_ = new List<byte>();
            ulong left = length;
            ulong skip = offset;
            foreach (VdfsExtent ext in extents)
            {
                if (left == 0 || ext.IsEmpty)
                {
                    continue;
                }
                ulong extBytes = ext.Length * (ulong)BlockSize;
                if (skip >= extBytes)
                {
                    skip -= extBytes;
                    continue;
                }
                ulong avail = extBytes - skip;
                ulong take = left < avail ? left : avail;
                byte[] raw = ReadBlocks(ext.Begin, ext.Length);
                int from = (int)skip;
                var slice = new byte[(int)take];
                Array.Copy(raw, from, slice, 0, (int)take);
                out_.AddRange(slice);
                left -= take;
                skip = 0;
            }
            if (left != 0)
            {
                throw new IOException("VDFS short read");
            }
            return out_.ToArray();
        }

        internal byte[] ReadForkData(VdfsCatRecord rec)
        {
            if (rec.Value.Length < VdfsConstants.FileRecordSize)
            {
                return new byte[0];
            }
            int forkOff = VdfsConstants.FolderRecordSize;
            ulong size = VdfsConstants.ReadU64(rec.Value, forkOff + VdfsConstants.ForkSizeOff);
            ulong blocks = VdfsConstants.ReadU64(rec.Value, forkOff + VdfsConstants.ForkBlocksOff);
            var fork = new byte[VdfsConstants.ForkDataLen];
            Array.Copy(rec.Value, forkOff + VdfsConstants.ForkDataOff, fork, 0, fork.Length);

            if (size == 0)
            {
                return new byte[0];
            }
            if (size > (ulong)int.MaxValue)
            {
                throw new IOException("VDFS file too large");
            }
            if (blocks == 0)
            {
                var plain = new byte[(int)size < fork.Length ? (int)size : fork.Length];
                Array.Copy(fork, plain, plain.Length);
                return plain;
            }
            List<VdfsExtent> extents = ResolveExtents(rec.ObjectId, size, blocks, fork);
            if (extents.Count == 0)
            {
                throw new IOException("VDFS file has no extents");
            }
            byte[] data = ReadConcat(extents, 0, size);
            return data;
        }

        internal string ReadSymlinkTarget(VdfsCatRecord rec)
        {
            byte[] data = ReadForkData(rec);
            string target = Encoding.UTF8.GetString(data).TrimEnd('\0');
            return target;
        }

        internal byte[] ReadCompressed(VdfsCatRecord rec)
        {
            if (rec.Value.Length < VdfsConstants.FileRecordSize)
            {
                throw new IOException("VDFS truncated file record");
            }
            int forkOff = VdfsConstants.FolderRecordSize;
            ulong unpacked = VdfsConstants.ReadU64(rec.Value, forkOff + VdfsConstants.ForkSizeOff);
            ulong blocks = VdfsConstants.ReadU64(rec.Value, forkOff + VdfsConstants.ForkBlocksOff);
            var fork = new byte[VdfsConstants.ForkDataLen];
            Array.Copy(rec.Value, forkOff + VdfsConstants.ForkDataOff, fork, 0, fork.Length);

            if (blocks == 0)
            {
                // Inline-compressed: type[3], packed-size byte, payload.
                string kind = Encoding.ASCII.GetString(fork, 0, 3);
                int packed = fork[3];
                int end = 4 + packed;
                if (end > fork.Length)
                {
                    end = fork.Length;
                }
                var payload = new byte[end - 4];
                Array.Copy(fork, 4, payload, 0, payload.Length);
                byte[] plain = Inflate(kind, payload);
                if ((ulong)plain.Length > unpacked)
                {
                    var cut = new byte[(int)unpacked];
                    Array.Copy(plain, cut, cut.Length);
                    return cut;
                }
                return plain;
            }

            // Extent blob: [chunks][comp-extent table][descriptor][auth?].
            List<VdfsExtent> extents = ResolveExtents(rec.ObjectId, blocks, blocks, fork);
            if (extents.Count == 0)
            {
                throw new IOException("VDFS compressed file has no extents");
            }
            ulong compTotal = blocks;
            byte[] blob = ReadConcat(extents, 0, compTotal);

            int descrOff = FindDescriptor(blob);
            if (descrOff < 0)
            {
                throw new IOException("VDFS compressed descriptor not found");
            }
            string magic = Encoding.ASCII.GetString(blob, descrOff + 1, 3);
            int extentsNum = VdfsConstants.ReadU16(blob, descrOff + 12);
            long tableOff = (long)descrOff - (long)extentsNum * VdfsConstants.CompExtentSize;
            if (tableOff < 0)
            {
                throw new IOException("VDFS compressed extent table out of range");
            }
            if (blob[descrOff] == (byte)'h')
            {
                // Single-stream layout: everything before the table inflates
                // to the whole file.
                var region = new byte[tableOff];
                Array.Copy(blob, region, region.Length);
                return Inflate(magic, region);
            }
            var result = new List<byte>();
            for (int i = 0; i < extentsNum; i++)
            {
                long eoff = tableOff + i * VdfsConstants.CompExtentSize;
                if (eoff + VdfsConstants.CompExtentSize > descrOff)
                {
                    break;
                }
                ushort flags = VdfsConstants.ReadU16(blob, (int)eoff + 2);
                uint len = VdfsConstants.ReadU32(blob, (int)eoff + 4);
                ulong start = VdfsConstants.ReadU64(blob, (int)eoff + 8);
                if (len == 0 || start >= (ulong)blob.Length)
                {
                    continue;
                }
                ulong avail = (ulong)blob.Length - start;
                int take = len < avail ? (int)len : (int)avail;
                if ((flags & VdfsConstants.ChunkFlagRaw) != 0)
                {
                    for (int k = 0; k < take; k++)
                    {
                        result.Add(blob[(int)start + k]);
                    }
                }
                else
                {
                    var chunk = new byte[take];
                    Array.Copy(blob, (int)start, chunk, 0, take);
                    result.AddRange(Inflate(magic, chunk));
                }
            }
            return result.ToArray();
        }

        private static int FindDescriptor(byte[] blob)
        {
            int[] lens = new int[] { VdfsConstants.CompDescrLen06, VdfsConstants.CompDescrLen05 };
            foreach (int len in lens)
            {
                if (blob.Length < len)
                {
                    continue;
                }
                int c = blob.Length - len;
                string name = Encoding.ASCII.GetString(blob, c + 9, 3);
                if (name != "Zip" && name != "Gzp" && name != "Lzo" && name != "Zst")
                {
                    continue;
                }
                ushort ver = VdfsConstants.ReadU16(blob, c + 14);
                if (ver == VdfsConstants.CompLayout05 || ver == VdfsConstants.CompLayout06)
                {
                    return c;
                }
            }
            return -1;
        }

        private static byte[] Inflate(string kind, byte[] payload)
        {
            if (kind == "Zip")
            {
                using (var ms = new MemoryStream(payload, false))
                using (var zs = new ZlibStream(ms, CompressionMode.Decompress, false))
                using (var out_ = new MemoryStream())
                {
                    zs.CopyTo(out_);
                    return out_.ToArray();
                }
            }
            if (kind == "Gzp")
            {
                return InflateGzip(payload);
            }
            throw new NotSupportedException("VDFS compression not supported: " + kind);
        }

        private static byte[] InflateGzip(byte[] payload)
        {
            // Minimal gzip framing (no preset dictionaries in mkfs output):
            // 10-byte header, optional extra/name/comment per flags, raw
            // deflate body, 8-byte trailer.
            if (payload.Length < 18 || payload[0] != 0x1F || payload[1] != 0x8B || payload[2] != 8)
            {
                throw new IOException("bad gzip header");
            }
            int pos = 10;
            byte flags = payload[3];
            if ((flags & 4) != 0)
            {
                if (pos + 2 > payload.Length)
                {
                    throw new IOException("bad gzip extra field");
                }
                pos += 2 + (payload[pos] | (payload[pos + 1] << 8));
            }
            if ((flags & 8) != 0)
            {
                while (pos < payload.Length && payload[pos] != 0)
                {
                    pos++;
                }
                pos++;
            }
            if ((flags & 16) != 0)
            {
                while (pos < payload.Length && payload[pos] != 0)
                {
                    pos++;
                }
                pos++;
            }
            if ((flags & 2) != 0)
            {
                pos += 2;
            }
            int end = payload.Length - 8;
            if (pos > end)
            {
                throw new IOException("bad gzip framing");
            }
            using (var ms = new MemoryStream(payload, pos, end - pos, false))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress, false))
            using (var out_ = new MemoryStream())
            {
                ds.CopyTo(out_);
                return out_.ToArray();
            }
        }
    }

    public struct VdfsBtreeHead
    {
        public uint Root;
        public ushort Height;
    }
}
