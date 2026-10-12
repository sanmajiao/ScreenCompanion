using System;
using System.Collections.Generic;
using System.Drawing;
namespace DesktopPortal
{
    // UI-thread-owned, bounded LRU. Stored copies outlive a directory refresh.
    internal sealed class DesktopImageCache : IDisposable
    {
        private sealed class Entry { internal string Key;internal Bitmap Image; }
        private readonly Dictionary<string,LinkedListNode<Entry>> entries=new Dictionary<string,LinkedListNode<Entry>>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<Entry> order=new LinkedList<Entry>();
        private readonly long limit;
        internal long Bytes { get; private set; }
        internal DesktopImageCache(long maximum){limit=maximum;}
        internal Bitmap Get(string key)
        {
            LinkedListNode<Entry> node;if(!entries.TryGetValue(key,out node))return null;
            order.Remove(node);order.AddLast(node);return (Bitmap)node.Value.Image.Clone();
        }
        internal void Put(string key,Bitmap image)
        {
            long size=(long)image.Width*image.Height*4;if(limit==0 || size>limit)return;
            LinkedListNode<Entry> previous;if(entries.TryGetValue(key,out previous))Remove(previous);
            while((Bytes+size>limit || entries.Count>=1024) && order.First!=null)Remove(order.First);
            var node=order.AddLast(new Entry{Key=key,Image=(Bitmap)image.Clone()});entries.Add(key,node);Bytes+=size;
        }
        private void Remove(LinkedListNode<Entry> node){entries.Remove(node.Value.Key);order.Remove(node);Bytes-=(long)node.Value.Image.Width*node.Value.Image.Height*4;node.Value.Image.Dispose();}
        public void Dispose(){while(order.First!=null)Remove(order.First);}
    }
}
