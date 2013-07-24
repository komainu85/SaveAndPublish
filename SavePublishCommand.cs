using System;
using System.Collections;
using System.Collections.Specialized;
using System.Linq;
using Sitecore;
using Sitecore.Collections;
using Sitecore.Configuration;
using Sitecore.ContentSearch;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.Data.Managers;
using Sitecore.Diagnostics;
using Sitecore.Globalization;
using Sitecore.Publishing;
using Sitecore.Shell.Framework.Commands;
using Sitecore.Web.UI.Sheer;
using Sitecore.Workflows;

namespace MikeRobbins.SaveAndPublish
{
    [Serializable]
    public class SavePublishCommand : Command
    {
        public SavePublishCommand()
        {
        }

        /// <summary>
        /// Executes the command in the specified context.
        /// </summary>
        /// <param name="context">The context.</param>
        public override void Execute(CommandContext context)
        {
            Assert.ArgumentNotNull(context, "context");
            if ((int)context.Items.Length != 1)
            {
                return;
            }
            Item items = context.Items[0];
            NameValueCollection nameValueCollection = new NameValueCollection();
            nameValueCollection["id"] = items.ID.ToString();
            nameValueCollection["language"] = items.Language.ToString();
            nameValueCollection["version"] = items.Version.ToString();
            nameValueCollection["workflow"] = "0";
            Context.ClientPage.Start(this, "Run", nameValueCollection);

            IndexFile(items);
        }

        /// <summary>
        /// Queries the state of the command.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>The state of the command.</returns>
        public override CommandState QueryState(CommandContext context)
        {
            //check edit permissions
            Error.AssertObject(context, "context");
            if ((int)context.Items.Length != 1)
            {
                return CommandState.Hidden;
            }
            Item items = context.Items[0];
            if (items.Appearance.ReadOnly)
            {
                return CommandState.Disabled;
            }
            if (!items.Access.CanWrite())
            {
                return CommandState.Disabled;
            }
            if (!Context.IsAdministrator && Settings.RequireLockBeforeEditing && base.HasField(items, FieldIDs.Lock) &&
                !items.Locking.HasLock() && !(items.TemplateID == TemplateIDs.Template))
            {
                return CommandState.Disabled;
            }

            return base.QueryState(context);
        }

        #region Workflow
        /// <summary>
        /// Checks the workflow.
        /// </summary>
        /// <param name="args">The arguments.</param>
        /// <param name="item">The item.</param>
        /// <returns>The workflow.</returns>
        private static bool CheckWorkflow(ClientPipelineArgs args, Item item)
        {
            Assert.ArgumentNotNull(args, "args");
            Assert.ArgumentNotNull(item, "item");
            if (args.Parameters["workflow"] == "1")
            {
                return true;
            }
            args.Parameters["workflow"] = "1";
            if (Context.ClientPage.Modified)
            {
                args.Parameters["__modified"] = "1";
            }
            if (args.IsPostBack)
            {
                if (args.Parameters["__modified"] == "1")
                {
                    Context.ClientPage.Modified = true;
                }
                if (args.Result != "yes")
                {
                    args.AbortPipeline();
                    return false;
                }
                args.IsPostBack = false;
                return true;
            }
            IWorkflowProvider workflowProvider = Client.ContentDatabase.WorkflowProvider;
            if (workflowProvider == null || (int)workflowProvider.GetWorkflows().Length <= 0)
            {
                return true;
            }
            IWorkflow workflow = workflowProvider.GetWorkflow(item);
            if (workflow == null)
            {
                return true;
            }
            WorkflowState state = workflow.GetState(item);
            if (state == null)
            {
                return true;
            }
            if (state.FinalState)
            {
                return true;
            }
            args.Parameters["workflow"] = "0";
            object[] displayName = new object[] { item.DisplayName, state.DisplayName };
            SheerResponse.Confirm(
                Translate.Text(
                    "The current item \"{0}\" is in the workflow state \"{1}\"\nand will not be published.\n\nAre you sure you want to publish?",
                    displayName));
            args.WaitForPostBack();
            return false;
        }
        #endregion

        #region Publishing
        /// Gets the targets.
        /// </summary>
        /// <returns></returns>
        private static Database[] GetTargets()
        {
            Item itemNotNull = Client.GetItemNotNull("/sitecore/system/publishing targets");
            ArrayList arrayLists = new ArrayList();
            ChildList children = itemNotNull.Children;
            foreach (Item child in children)
            {
                string item = child["Target database"];
                Database database = Factory.GetDatabase(item, false);
                if (database == null)
                {
                    continue;
                }
                arrayLists.Add(database);
            }
            return Assert.ResultNotNull<Database[]>(arrayLists.ToArray(typeof(Database)) as Database[]);
        }

        /// <summary>
        /// Runs the specified args.
        /// </summary>
        /// <param name="args">The args.</param>
        protected void Run(ClientPipelineArgs args)
        {
            Assert.ArgumentNotNull(args, "args");
            string item = args.Parameters["id"];
            string str = args.Parameters["language"];
            string item1 = args.Parameters["version"];
            Item item2 = Client.ContentDatabase.Items[item, Language.Parse(str), Sitecore.Data.Version.Parse(item1)];
            if (item2 == null)
            {
                SheerResponse.Alert("Item not found.", new string[0]);
                return;
            }
            if (!CheckWorkflow(args, item2))
            {
                return;
            }
            if (!SheerResponse.CheckModified())
            {
                return;
            }
            if (!args.IsPostBack)
            {
                object[] displayName = new object[] { item2.DisplayName };
                SheerResponse.Confirm(
                    Translate.Text(
                        "Are you sure you want to publish \"{0}\"\nin every language to every publishing target?",
                        displayName));
                args.WaitForPostBack();
            }
            else
            {
                if (args.Result == "yes")
                {
                    Database[] targets = GetTargets();
                    if ((int)targets.Length == 0)
                    {
                        SheerResponse.Alert("No target databases were found for publishing.", new string[0]);
                        return;
                    }
                    LanguageCollection languages = LanguageManager.GetLanguages(Context.ContentDatabase);
                    if (languages == null || languages.Count == 0)
                    {
                        SheerResponse.Alert("No languages were found for publishing.", new string[0]);
                        return;
                    }
                    string[] strArrays = new string[] { AuditFormatter.FormatItem(item2) };
                    Log.Audit(this, "Publish item now: {0}", strArrays);
                    PublishManager.PublishItem(item2, targets, languages.ToArray(), false, true);
                    SheerResponse.Alert("The item is being published.", new string[0]);
                    return;
                }
            }
        }
        #endregion

        #region Indexing
        private void IndexFile(Item item)
        {
            var tempItem = (SitecoreIndexableItem)item;

            var webIndex = ContentSearchManager.Indexes.FirstOrDefault(x => x.Name.ToLower().Contains("web"));

            if (webIndex != null)
            {
                webIndex.Refresh(tempItem);
            }
        }
        #endregion



    }
}
