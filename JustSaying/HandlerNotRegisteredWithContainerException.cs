﻿using System;

namespace JustSaying
{
    public class HandlerNotRegisteredWithContainerException :Exception
    {
        public HandlerNotRegisteredWithContainerException(string message) : base(message){}
        public HandlerNotRegisteredWithContainerException(string message, Exception inner) : base(message, inner){}
    }
}
