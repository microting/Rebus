﻿using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rebus.Extensions;
using Rebus.Messages;

namespace Rebus.Serialization
{
    /// <summary>
    /// Implementation of <see cref="ISerializer"/> that uses Newtonsoft JSON.NET internally, with some pretty robust settings
    /// (i.e. full type info is included in the serialized format in order to support deserializing "unknown" types like
    /// implementations of interfaces, etc)
    /// </summary>
    internal class JsonSerializer : ISerializer
    {
        const string JsonUtf8ContentType = "application/json;charset=utf-8";

        static readonly JsonSerializerSettings DefaultSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All
        };

        static readonly Encoding DefaultEncoding = Encoding.UTF8;

        readonly JsonSerializerSettings _settings;

        public JsonSerializer()
        {
            _settings = DefaultSettings;
        }

        internal JsonSerializer(JsonSerializerSettings jsonSerializerSettings)
        {
            _settings = jsonSerializerSettings;
        }

        public async Task<TransportMessage> Serialize(Message message)
        {
            var jsonText = JsonConvert.SerializeObject(message.Body, _settings);
            var bytes = DefaultEncoding.GetBytes(jsonText);
            var headers = message.Headers.Clone();
            var messageType = message.Body.GetType();
            headers[Headers.Type] = messageType.GetSimpleAssemblyQualifiedName();
            headers[Headers.ContentType] = JsonUtf8ContentType;
            return new TransportMessage(headers, bytes);
        }

        public async Task<Message> Deserialize(TransportMessage transportMessage)
        {
            var contentType = transportMessage.Headers.GetValue(Headers.ContentType);

            if (contentType != JsonUtf8ContentType)
            {
                throw new FormatException(string.Format("Unknown content type: '{0}' - must be '{1}' for the JSON serialier to work", contentType, JsonUtf8ContentType));
            }

            var bodyString = DefaultEncoding.GetString(transportMessage.Body);
            var bodyObject = JsonConvert.DeserializeObject(bodyString, _settings);
            var headers = transportMessage.Headers.Clone();
            return new Message(headers, bodyObject);
        }
    }
}