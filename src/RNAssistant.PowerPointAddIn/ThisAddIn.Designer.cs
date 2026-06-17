#pragma warning disable 414
namespace RNAssistant.PowerPointAddIn
{
    [Microsoft.VisualStudio.Tools.Applications.Runtime.StartupObjectAttribute(0)]
    [global::System.Security.Permissions.PermissionSetAttribute(global::System.Security.Permissions.SecurityAction.Demand, Name = "FullTrust")]
    public sealed partial class ThisAddIn : Microsoft.Office.Tools.AddInBase
    {
        internal Microsoft.Office.Tools.CustomTaskPaneCollection CustomTaskPanes;
        internal Microsoft.Office.Interop.PowerPoint.Application Application;

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
        public ThisAddIn(global::Microsoft.Office.Tools.Factory factory, global::System.IServiceProvider serviceProvider)
            : base(factory, serviceProvider, "AddIn", "ThisAddIn")
        {
            Globals.Factory = factory;
        }

        protected override void Initialize()
        {
            base.Initialize();
            Application = GetHostItem<Microsoft.Office.Interop.PowerPoint.Application>(typeof(Microsoft.Office.Interop.PowerPoint.Application), "Application");
            Globals.ThisAddIn = this;
            global::System.Windows.Forms.Application.EnableVisualStyles();
            InitializeControls();
        }

        protected override void FinishInitialization()
        {
            InternalStartup();
            OnStartup();
        }

        protected override void InitializeDataBindings()
        {
            BeginInit();
            if (CustomTaskPanes != null)
            {
                CustomTaskPanes.BeginInit();
                CustomTaskPanes.EndInit();
            }
            EndInit();
        }

        private void InitializeControls()
        {
            CustomTaskPanes = Globals.Factory.CreateCustomTaskPaneCollection(null, null, "CustomTaskPanes", "CustomTaskPanes", this);
        }

        protected override void OnShutdown()
        {
            if (CustomTaskPanes != null)
            {
                CustomTaskPanes.Dispose();
            }
            base.OnShutdown();
        }
    }

    internal sealed partial class Globals
    {
        private static ThisAddIn _thisAddIn;
        private static global::Microsoft.Office.Tools.Factory _factory;

        internal static ThisAddIn ThisAddIn
        {
            get { return _thisAddIn; }
            set
            {
                if (_thisAddIn == null)
                {
                    _thisAddIn = value;
                }
                else
                {
                    throw new global::System.NotSupportedException();
                }
            }
        }

        internal static global::Microsoft.Office.Tools.Factory Factory
        {
            get { return _factory; }
            set
            {
                if (_factory == null)
                {
                    _factory = value;
                }
                else
                {
                    throw new global::System.NotSupportedException();
                }
            }
        }
    }
}

