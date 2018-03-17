﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace i2MFCS.WMS.Core.MFCS_Proxy {
    using System.Runtime.Serialization;
    using System;
    
    
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.CodeDom.Compiler.GeneratedCodeAttribute("System.Runtime.Serialization", "4.0.0.0")]
    [System.Runtime.Serialization.DataContractAttribute(Name="DTOCommand", Namespace="http://schemas.datacontract.org/2004/07/WcfService.DTO")]
    [System.SerializableAttribute()]
    public partial class DTOCommand : object, System.Runtime.Serialization.IExtensibleDataObject, System.ComponentModel.INotifyPropertyChanged {
        
        [System.NonSerializedAttribute()]
        private System.Runtime.Serialization.ExtensionDataObject extensionDataField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private int IDField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private int Order_IDField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private string SourceField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private int StatusField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private int TU_IDField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private string TargetField;
        
        [System.Runtime.Serialization.OptionalFieldAttribute()]
        private System.DateTime TimeField;
        
        [global::System.ComponentModel.BrowsableAttribute(false)]
        public System.Runtime.Serialization.ExtensionDataObject ExtensionData {
            get {
                return this.extensionDataField;
            }
            set {
                this.extensionDataField = value;
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public int ID {
            get {
                return this.IDField;
            }
            set {
                if ((this.IDField.Equals(value) != true)) {
                    this.IDField = value;
                    this.RaisePropertyChanged("ID");
                }
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public int Order_ID {
            get {
                return this.Order_IDField;
            }
            set {
                if ((this.Order_IDField.Equals(value) != true)) {
                    this.Order_IDField = value;
                    this.RaisePropertyChanged("Order_ID");
                }
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public string Source {
            get {
                return this.SourceField;
            }
            set {
                if ((object.ReferenceEquals(this.SourceField, value) != true)) {
                    this.SourceField = value;
                    this.RaisePropertyChanged("Source");
                }
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public int Status {
            get {
                return this.StatusField;
            }
            set {
                if ((this.StatusField.Equals(value) != true)) {
                    this.StatusField = value;
                    this.RaisePropertyChanged("Status");
                }
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public int TU_ID {
            get {
                return this.TU_IDField;
            }
            set {
                if ((this.TU_IDField.Equals(value) != true)) {
                    this.TU_IDField = value;
                    this.RaisePropertyChanged("TU_ID");
                }
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public string Target {
            get {
                return this.TargetField;
            }
            set {
                if ((object.ReferenceEquals(this.TargetField, value) != true)) {
                    this.TargetField = value;
                    this.RaisePropertyChanged("Target");
                }
            }
        }
        
        [System.Runtime.Serialization.DataMemberAttribute()]
        public System.DateTime Time {
            get {
                return this.TimeField;
            }
            set {
                if ((this.TimeField.Equals(value) != true)) {
                    this.TimeField = value;
                    this.RaisePropertyChanged("Time");
                }
            }
        }
        
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        
        protected void RaisePropertyChanged(string propertyName) {
            System.ComponentModel.PropertyChangedEventHandler propertyChanged = this.PropertyChanged;
            if ((propertyChanged != null)) {
                propertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
            }
        }
    }
    
    [System.CodeDom.Compiler.GeneratedCodeAttribute("System.ServiceModel", "4.0.0.0")]
    [System.ServiceModel.ServiceContractAttribute(ConfigurationName="MFCS_Proxy.IWMS")]
    public interface IWMS {
        
        [System.ServiceModel.OperationContractAttribute(Action="http://tempuri.org/IWMS/MFCS_Submit", ReplyAction="http://tempuri.org/IWMS/MFCS_SubmitResponse")]
        void MFCS_Submit(i2MFCS.WMS.Core.MFCS_Proxy.DTOCommand[] cmds);
        
        [System.ServiceModel.OperationContractAttribute(Action="http://tempuri.org/IWMS/MFCS_Submit", ReplyAction="http://tempuri.org/IWMS/MFCS_SubmitResponse")]
        System.Threading.Tasks.Task MFCS_SubmitAsync(i2MFCS.WMS.Core.MFCS_Proxy.DTOCommand[] cmds);
    }
    
    [System.CodeDom.Compiler.GeneratedCodeAttribute("System.ServiceModel", "4.0.0.0")]
    public interface IWMSChannel : i2MFCS.WMS.Core.MFCS_Proxy.IWMS, System.ServiceModel.IClientChannel {
    }
    
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.CodeDom.Compiler.GeneratedCodeAttribute("System.ServiceModel", "4.0.0.0")]
    public partial class WMSClient : System.ServiceModel.ClientBase<i2MFCS.WMS.Core.MFCS_Proxy.IWMS>, i2MFCS.WMS.Core.MFCS_Proxy.IWMS {
        
        public WMSClient() {
        }
        
        public WMSClient(string endpointConfigurationName) : 
                base(endpointConfigurationName) {
        }
        
        public WMSClient(string endpointConfigurationName, string remoteAddress) : 
                base(endpointConfigurationName, remoteAddress) {
        }
        
        public WMSClient(string endpointConfigurationName, System.ServiceModel.EndpointAddress remoteAddress) : 
                base(endpointConfigurationName, remoteAddress) {
        }
        
        public WMSClient(System.ServiceModel.Channels.Binding binding, System.ServiceModel.EndpointAddress remoteAddress) : 
                base(binding, remoteAddress) {
        }
        
        public void MFCS_Submit(i2MFCS.WMS.Core.MFCS_Proxy.DTOCommand[] cmds) {
            base.Channel.MFCS_Submit(cmds);
        }
        
        public System.Threading.Tasks.Task MFCS_SubmitAsync(i2MFCS.WMS.Core.MFCS_Proxy.DTOCommand[] cmds) {
            return base.Channel.MFCS_SubmitAsync(cmds);
        }
    }
}
