﻿//
// Copyright (c) 2008-2009, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;

namespace DiscUtils.Ntfs
{
    internal sealed class StandardInformation : IByteArraySerializable, IDiagnosticTracer
    {
        public DateTime CreationTime;
        public DateTime ModificationTime;
        public DateTime MftChangedTime;
        public DateTime LastAccessTime;
        public FileAttributeFlags FileAttributes;
        public uint MaxVersions;
        public uint Version;
        public uint ClassId;
        public uint OwnerId;
        public uint SecurityId;
        public ulong QuotaCharged;
        public ulong UpdateSequenceNumber;

        private bool _haveExtraFields;



        #region IByteArraySerializable Members

        public void ReadFrom(byte[] buffer, int offset)
        {
            CreationTime = DateTime.FromFileTimeUtc(Utilities.ToInt64LittleEndian(buffer, 0x00));
            ModificationTime = DateTime.FromFileTimeUtc(Utilities.ToInt64LittleEndian(buffer, 0x08));
            MftChangedTime = DateTime.FromFileTimeUtc(Utilities.ToInt64LittleEndian(buffer, 0x10));
            LastAccessTime = DateTime.FromFileTimeUtc(Utilities.ToInt64LittleEndian(buffer, 0x18));
            FileAttributes = (FileAttributeFlags)Utilities.ToUInt32LittleEndian(buffer, 0x20);
            MaxVersions = Utilities.ToUInt32LittleEndian(buffer, 0x24);
            Version = Utilities.ToUInt32LittleEndian(buffer, 0x28);
            ClassId = Utilities.ToUInt32LittleEndian(buffer, 0x2C);

            if (buffer.Length > 0x30)
            {
                _haveExtraFields = true;
                OwnerId = Utilities.ToUInt32LittleEndian(buffer, 0x30);
                SecurityId = Utilities.ToUInt32LittleEndian(buffer, 0x34);
                QuotaCharged = Utilities.ToUInt64LittleEndian(buffer, 0x38);
                UpdateSequenceNumber = Utilities.ToUInt64LittleEndian(buffer, 0x40);
            }
        }

        public void WriteTo(byte[] buffer, int offset)
        {
            Utilities.WriteBytesLittleEndian(CreationTime.ToFileTimeUtc(), buffer, 0x00);
            Utilities.WriteBytesLittleEndian(ModificationTime.ToFileTimeUtc(), buffer, 0x08);
            Utilities.WriteBytesLittleEndian(MftChangedTime.ToFileTimeUtc(), buffer, 0x10);
            Utilities.WriteBytesLittleEndian(LastAccessTime.ToFileTimeUtc(), buffer, 0x18);
            Utilities.WriteBytesLittleEndian((uint)FileAttributes, buffer, 0x20);
            Utilities.WriteBytesLittleEndian(MaxVersions, buffer, 0x24);
            Utilities.WriteBytesLittleEndian(Version, buffer, 0x28);
            Utilities.WriteBytesLittleEndian(ClassId, buffer, 0x2C);

            if (_haveExtraFields)
            {
                Utilities.WriteBytesLittleEndian(OwnerId, buffer, 0x30);
                Utilities.WriteBytesLittleEndian(SecurityId, buffer, 0x34);
                Utilities.WriteBytesLittleEndian(QuotaCharged, buffer, 0x38);
                Utilities.WriteBytesLittleEndian(UpdateSequenceNumber, buffer, 0x38);
            }
        }

        public int Size
        {
            get { return _haveExtraFields ? 0x48 : 0x30; }
        }

        #endregion

        #region IDiagnosticTracer Members

        public void Dump(TextWriter writer, string indent)
        {
            writer.WriteLine(indent + "      Creation Time: " + CreationTime);
            writer.WriteLine(indent + "  Modification Time: " + ModificationTime);
            writer.WriteLine(indent + "   MFT Changed Time: " + MftChangedTime);
            writer.WriteLine(indent + "   Last Access Time: " + LastAccessTime);
            writer.WriteLine(indent + "   File Permissions: " + FileAttributes);
            writer.WriteLine(indent + "       Max Versions: " + MaxVersions);
            writer.WriteLine(indent + "            Version: " + Version);
            writer.WriteLine(indent + "           Class Id: " + ClassId);
            writer.WriteLine(indent + "        Security Id: " + SecurityId);
            writer.WriteLine(indent + "      Quota Charged: " + QuotaCharged);
            writer.WriteLine(indent + "     Update Seq Num: " + UpdateSequenceNumber);
        }

        #endregion
    }
}