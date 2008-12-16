﻿//
// Copyright (c) 2008, Kenneth Bell
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

using System.Collections.Generic;
using System.IO;
using DiscUtils.Ntfs.Attributes;

namespace DiscUtils.Ntfs
{
    internal class File
    {
        protected NtfsFileSystem _fileSystem;
        protected FileRecord _baseRecord;

        public File(NtfsFileSystem fileSystem, FileRecord baseRecord)
        {
            _fileSystem = fileSystem;
            _baseRecord = baseRecord;
        }

        public uint MasterFileTableIndex
        {
            get { return _baseRecord.MasterFileTableIndex; }
        }

        /// <summary>
        /// Gets the first (if more than one) instance of an unnamed attribute.
        /// </summary>
        /// <param name="type">The attribute type</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public BaseAttribute GetAttribute(AttributeType type)
        {
            return GetAttribute(type, null);
        }

        /// <summary>
        ///  Gets all instances of an unnamed attribute.
        /// </summary>
        /// <param name="type">The attribute type</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public BaseAttribute[] GetAttributes(AttributeType type)
        {
            List<BaseAttribute> matches = new List<BaseAttribute>();

            FileAttributeRecord attrListRec = _baseRecord.GetAttribute(AttributeType.AttributeList);
            if (attrListRec != null)
            {
                AttributeListAttribute attrList = new AttributeListAttribute(_fileSystem, attrListRec);
                foreach (var r in attrList.Records)
                {
                    if (r.Type == type && r.Name == null)
                    {
                        FileRecord fileRec = _fileSystem.MasterFileTable.GetRecord(r.BaseFileReference);
                        matches.Add(BaseAttribute.FromRecord(_fileSystem, fileRec.GetAttribute(type)));
                    }
                }
            }
            else
            {
                foreach (FileAttributeRecord attrRec in _baseRecord.Attributes)
                {
                    if (attrRec.AttributeType == type && attrRec.Name == null)
                    {
                        matches.Add(BaseAttribute.FromRecord(_fileSystem, attrRec));
                    }
                }
            }

            return matches.ToArray();
        }

        /// <summary>
        ///  Gets the first (if more than one) instance of a named attribute.
        /// </summary>
        /// <param name="type">The attribute type</param>
        /// <param name="name">The attribute's name</param>
        /// <returns>The attribute of <c>null</c>.</returns>
        public BaseAttribute GetAttribute(AttributeType type, string name)
        {
            FileAttributeRecord targetAttribute = null;

            FileAttributeRecord attrListRec = _baseRecord.GetAttribute(AttributeType.AttributeList);
            if (attrListRec != null)
            {
                AttributeListAttribute attrList = new AttributeListAttribute(_fileSystem, attrListRec);
                foreach (var r in attrList.Records)
                {
                    if (r.Type == type && r.Name == name)
                    {
                        FileRecord fileRec = _fileSystem.MasterFileTable.GetRecord(r.BaseFileReference);
                        targetAttribute = fileRec.GetAttribute(type);
                        break;
                    }
                }
            }
            else
            {
                targetAttribute = _baseRecord.GetAttribute(type, name);
            }

            if (targetAttribute != null)
            {
                return BaseAttribute.FromRecord(_fileSystem, targetAttribute);
            }
            else
            {
                return null;
            }
        }

        public Stream OpenAttribute(AttributeType type, FileAccess access)
        {
            BaseAttribute attr = GetAttribute(type);

            if (attr == null)
            {
                throw new IOException("No such attribute: " + type);
            }

            return attr.Open(access);
        }

        public Stream OpenAttribute(AttributeType type, string name, FileAccess access)
        {
            BaseAttribute attr = GetAttribute(type, name);

            if (attr == null)
            {
                throw new IOException("No such attribute: " + type + "(" + name + ")");
            }

            return attr.Open(access);
        }

        public FileAttributes FileAttributes
        {
            get
            {
                return ((FileNameAttribute)GetAttribute(AttributeType.FileName)).Attributes;
            }
        }

        public DirectoryEntry DirectoryEntry
        {
            get
            {
                FileNameAttribute fnAttr = (FileNameAttribute)GetAttribute(AttributeType.FileName);
                FileAttributeRecord record = _baseRecord.GetAttribute(AttributeType.FileName);
                return new DirectoryEntry(new FileReference(_baseRecord.MasterFileTableIndex), fnAttr.FileNameRecord);
            }
        }


        public virtual void Dump(TextWriter writer, string indent)
        {
            writer.WriteLine(indent + "FILE (" + _baseRecord.ToString() + ")");
            writer.WriteLine(indent + "  File Number: " + _baseRecord.MasterFileTableIndex);

            foreach (FileAttributeRecord attrRec in _baseRecord.Attributes)
            {
                BaseAttribute.FromRecord(_fileSystem, attrRec).Dump(writer, indent + "  ");
            }
        }

        public override string ToString()
        {
            BaseAttribute[] attrs = GetAttributes(AttributeType.FileName);

            int longest = 0;
            string longName = attrs[0].ToString();

            for (int i = 1; i < attrs.Length; ++i)
            {
                string name = attrs[i].ToString();

                if (Utilities.Is8Dot3(longName))
                {
                    longest = i;
                    longName = name;
                }
            }

            return longName;
        }
    }
}