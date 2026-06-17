#pragma warning disable 414
namespace RNAssistant.ExcelAddIn
{
    [Microsoft.VisualStudio.Tools.Applications.Runtime.StartupObjectAttribute(0)]
    [global::System.Security.Permissions.PermissionSetAttribute(global::System.Security.Permissions.SecurityAction.Demand, Name = "FullTrust")]
    public sealed partial class ThisAddIn : Microsoft.Office.Tools.AddInBase
    {
        internal Microsoft.Office.Tools.CustomTaskPaneCollection CustomTaskPanes;
        internal Microsoft.Office.Interop.Excel.Application Application;

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
        public ThisAddIn(global::Microsoft.Office.Tools.Excel.ApplicationFactory factory, global::System.IServiceProvider serviceProvider)
            : base(factory, serviceProvider, "AddIn", "ThisAddIn")
        {
            Globals.Factory = factory;
        }

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
        protected override void Initialize()
        {
            base.Initialize();
            Application = GetHostItem<Microsoft.Office.Interop.Excel.Application>(typeof(Microsoft.Office.Interop.Excel.Application), "Application");
            Globals.ThisAddIn = this;
            global::System.Windows.Forms.Application.EnableVisualStyles();
            InitializeControls();
        }

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
        protected override void FinishInitialization()
        {
            InternalStartup();
            OnStartup();
        }

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
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

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
        private void InitializeControls()
        {
            CustomTaskPanes = Globals.Factory.CreateCustomTaskPaneCollection(null, null, "CustomTaskPanes", "CustomTaskPanes", this);
        }

        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)]
        protected override void OnShutdown()
        {
            if (CustomTaskPanes != null)
            {
                CustomTaskPanes.Dispose();
            }
            base.OnShutdown();
        }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    internal sealed partial class Globals
    {
        private static ThisAddIn _thisAddIn;
        private static global::Microsoft.Office.Tools.Excel.ApplicationFactory _factory;

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

        internal static global::Microsoft.Office.Tools.Excel.ApplicationFactory Factory
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

