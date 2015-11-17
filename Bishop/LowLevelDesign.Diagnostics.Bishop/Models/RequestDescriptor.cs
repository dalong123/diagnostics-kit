﻿using Fiddler;

namespace LowLevelDesign.Diagnostics.Bishop.Models
{
    public sealed class RequestDescriptor
    {
        private readonly bool isLocal;
        private readonly bool isHttpsConnect;
        private readonly Session fiddlerSession;

        public RequestDescriptor(Session oSession)
        {
            isLocal = oSession.HostnameIs("localhost") || oSession.HostnameIs("127.0.0.1") || 
                oSession.HostnameIs("[::1]");
            isHttpsConnect = oSession.HTTPMethodIs("CONNECT");
            fiddlerSession = oSession;
        }

        public bool IsLocal { get { return isLocal; } }

        public bool IsHttps { get { return fiddlerSession.isHTTPS; } }

        public bool IsHttpsConnect { get { return isHttpsConnect; } }

        public Session FiddlerSession { get { return fiddlerSession; } }
    }
}
