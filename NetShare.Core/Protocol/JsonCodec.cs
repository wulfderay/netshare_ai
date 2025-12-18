using System;
using System.Text;
using System.Web.Script.Serialization;

namespace NetShare.Core.Protocol
{
    public sealed class JsonCodec
    {
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public byte[] Encode(object obj)
        {
            var json = _serializer.Serialize(obj);
            return Encoding.UTF8.GetBytes(json);
        }

        public T Decode<T>(byte[] utf8Json)
        {
            if (utf8Json == null) throw new ArgumentNullException(nameof(utf8Json));
            var json = Encoding.UTF8.GetString(utf8Json);
            return _serializer.Deserialize<T>(json);
        }

        public object DecodeUntyped(byte[] utf8Json)
        {
            if (utf8Json == null) throw new ArgumentNullException(nameof(utf8Json));
            var json = Encoding.UTF8.GetString(utf8Json);
            return _serializer.DeserializeObject(json);
        }
    }
}
