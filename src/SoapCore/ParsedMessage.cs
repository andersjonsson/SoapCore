using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;

namespace SoapCore
{
	public class ParsedMessage : Message
	{
		private readonly MessageHeaders _headers;
		private readonly MessageProperties _properties;
		private readonly MessageVersion _version;
		private readonly XDocument _body;
		private readonly bool _isEmpty;

		public ParsedMessage(MessageHeaders headers, MessageProperties properties, MessageVersion version, XDocument body, bool isEmpty)
		{
			_headers = headers;
			_properties = properties;
			_version = version;
			_body = body;
			_isEmpty = isEmpty;
		}

		public override MessageHeaders Headers => _headers;
		public override MessageProperties Properties => _properties;
		public override MessageVersion Version => _version;

		public override bool IsEmpty => _isEmpty;

		public static ParsedMessage FromXmlReaderAsync(XmlReader reader, MessageVersion version)
		{
			if (reader == null)
			{
				throw new ArgumentNullException(nameof(reader));
			}

			if (version == null)
			{
				throw new ArgumentNullException(nameof(version));
			}

			var envelope = XDocument.Load(reader);
			var headers = ExtractSoapHeaders(envelope, version);

			//var properties = ExtractSoapProperties(httpRequest);
			(var body, var isEmpty) = ExtractSoapBody(envelope, version);

			return new ParsedMessage(headers, new MessageProperties(), version, body, isEmpty);
		}

		public static ParsedMessage FromBodyWriter(BodyWriter writer, MessageVersion version, string action)
		{
			if (writer == null)
			{
				throw new ArgumentNullException(nameof(writer));
			}

			if (version == null)
			{
				throw new ArgumentNullException(nameof(version));
			}

			StringBuilder sb = new StringBuilder();

			var w = XmlDictionaryWriter.CreateDictionaryWriter(XmlWriter.Create(sb));
			writer.WriteBodyContents(w);
			w.Flush();
			var s = sb.ToString();

			using (var xmlWriter = XmlWriter.Create(sb, new XmlWriterSettings()))
			{
				using (var xmlDictionaryWriter = XmlDictionaryWriter.CreateDictionaryWriter(xmlWriter))
				{
					writer.WriteBodyContents(xmlDictionaryWriter);
				}
			}

			var body = XDocument.Parse(s);

			var mess = new ParsedMessage(new MessageHeaders(version), new MessageProperties(), version, body, body.Root.IsEmpty);
			if (action != null)
			{
				mess.Headers.Action = action;
			}

			return mess;
		}

		public XDocument GetBodyAsXDocument()
		{
			return _body;
		}

		public override string ToString()
		{
			return _body?.ToString() ?? "<Empty Body>";
		}

		protected override void OnWriteBodyContents(XmlDictionaryWriter writer)
		{
			//I have to set this to make sure that the operation succeeds. Since this Message implementation has no stream it can safely be written multiple times
			typeof(Message).GetField("<State>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(this, MessageState.Created);

			using (var reader = GetReaderAtBodyContents())
			{
				writer.WriteNode(reader, true);
			}
		}

		protected override XmlDictionaryReader OnGetReaderAtBodyContents()
		{
			var reader = new XDocumentXmlReader(_body);

			XNamespace soapNs = _version.Envelope.Namespace();

			if (_body.Descendants(soapNs + "Body").Any())
			{
				while (reader.Read()) // Advance through the document
				{
					if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Body" && reader.NamespaceURI.Equals(soapNs.ToString(), StringComparison.OrdinalIgnoreCase))
					{
						break;
					}
				}

				while (reader.Read() && reader.NodeType != XmlNodeType.Element && reader.NodeType != XmlNodeType.EndElement)
				{
				}
			}
			else //The message has been created without a surrounding envelope
			{
				reader.Read();
			}

			return XmlDictionaryReader.CreateDictionaryReader(reader);
		}

		protected override void OnClose()
		{
			_properties.Dispose();
			base.OnClose();
		}

		protected override MessageBuffer OnCreateBufferedCopy(int maxBufferSize)
		{
			return new ParsedMessageBuffer(this);
		}

		/// <summary>
		/// Extracts SOAP headers from the SOAP message body.
		/// </summary>
		private static MessageHeaders ExtractSoapHeaders(XDocument envelope, MessageVersion version)
		{
			var headers = new MessageHeaders(version);
			var root = envelope.Root;
			if (root == null)
			{
				return headers;
			}

			XNamespace soapNs = version.Envelope.Namespace();
			var headerNode = root.Element(soapNs + "Header");
			if (headerNode != null)
			{
				foreach (var element in headerNode.Elements())
				{
					var header = new ParsedMessageHeader(element.Name.LocalName, element.Name.NamespaceName, element.Elements().ToArray(), element.Value, element.Attributes().ToArray());
					headers.Add(header);
				}
			}

			return headers;
		}

		/// <summary>
		/// Extracts only the SOAP Body content.
		/// </summary>
		private static (XDocument, bool isEmpty) ExtractSoapBody(XDocument envelope, MessageVersion version)
		{
			var root = envelope.Root;
			if (root == null)
			{
				return (new XDocument(), true);
			}

			XNamespace soapNs = version.Envelope.Namespace();
			var bodyNode = root.Element(soapNs + "Body");
			if (bodyNode == null)
			{
				return (new XDocument(), true);
			}

			//return new XDocument(bodyNode.Elements().FirstOrDefault());
			return (new XDocument(bodyNode), bodyNode.IsEmpty);
		}

		/// <summary>
		/// Extracts SOAP-specific properties.
		/// </summary>
		private static MessageProperties ExtractSoapProperties(HttpRequest httpRequest)
		{
			var properties = new MessageProperties
			{
				["HttpMethod"] = httpRequest.Method,
				["RequestUri"] = httpRequest.Path + httpRequest.QueryString
			};

			if (httpRequest.ContentType != null)
			{
				properties["Content-Type"] = httpRequest.ContentType;
			}

			return properties;
		}
	}
}
