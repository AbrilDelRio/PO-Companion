import { IInputs, IOutputs } from "./generated/ManifestTypes";

type ControlState = "idle" | "ready" | "uploading" | "processing" | "success" | "error";
type ImportMode = "mpp" | "excel" | "project" | "projectOnline";
type PwaConnectionState = "notConfigured" | "notConnected" | "connecting" | "ready" | "connected" | "error";
type DataverseCloudName = "Commercial" | "GCC" | "GCCHigh" | "DoD" | "China";

interface XrmGlobalContextHost {
    Xrm?: {
        Utility?: {
            getGlobalContext?: () => { getClientUrl?: () => string };
        };
    };
}

interface XrmNavigationApi {
    navigateTo(
        pageInput: {
            pageType: "custom";
            name: string;
            entityName?: string;
            recordId?: string;
        },
        navigationOptions: {
            target: 2;
            position: 1;
            width: { value: number; unit: "px" };
            height: { value: number; unit: "px" };
            title: string;
        }
    ): Promise<void>;
}

interface ProjectOption {
    id: string;
    name: string;
}

interface SourceProjectRow extends ProjectOption {
    startDate: string | null;
    finishDate: string | null;
    ownerName: string | null;
}

/** A Dataverse environment the copy can be sent to. */
interface EnvironmentTarget {
    name: string;
    environmentUrl: string;
    environmentApiUrl: string;
    cloud: DataverseCloudName;
    host: string;
}

interface CopyProjectPayload {
    sourceProjectId: string;
    targetEnvironment: Omit<EnvironmentTarget, "host">;
    // Legacy routing fields: the Function keeps using them to reach the SOURCE environment (the
    // one this control runs in).
    environmentUrl: string;
    environmentApiUrl: string;
    cloud: DataverseCloudName;
    correlationId: string;
}

interface ProjectCopyOutcome {
    projectId: string;
    projectName: string;
    succeeded: boolean;
    message: string;
    correlationId: string | null;
}

interface PwaTask {
    id: string;
    name: string;
    startDate: string | null;
    finishDate: string | null;
    duration: string | null;
}

interface PwaProject {
    id: string;
    name: string;
    startDate: string | null;
    finishDate: string | null;
    ownerName: string | null;
    tasks: PwaTask[] | null;
}

interface HttpResult {
    status: number;
    body: unknown;
}

interface DurableStatusResponse {
    runtimeStatus?: string;
    output?: unknown;
    customStatus?: unknown;
    error?: string;
}

interface StatusRequest {
    url: string;
    functionKey: string;
}

interface HostEnvironment {
    environmentUrl: string;
    environmentApiUrl: string;
    cloud: DataverseCloudName;
    host: string;
}

const DEFAULT_MAX_FILE_SIZE_MB = 210;
const DEFAULT_TIMEOUT_SECONDS = 600;
const BYTES_PER_MB = 1024 * 1024;
const PROJECT_SEARCH_DELAY_MS = 350;
const PROJECT_SEARCH_MIN_LENGTH = 2;
const PROJECT_RESULT_LIMIT = 10;
const SOURCE_PROJECT_PAGE_SIZE = 50;
const ENVIRONMENTS_REQUEST_TIMEOUT_MS = 30000;
const STATUS_POLL_INTERVAL_MS = 3000;
const TEST_CONNECTION_TIMEOUT_MS = 30000;
const TEST_CONNECTION_RESULT_VISIBILITY_MS = 15000;
const PO_COPY_PROJECT_CONFIGURATION_ENTITY = "mfd_pocopyprojectconfiguration";
const PO_COPY_PROJECT_CONFIGURATION_ID_FIELD = "mfd_pocopyprojectconfigurationid";
const PWA_SHAREPOINT_URL_FIELD = "mfd_posharepointurl";

// Same host suffixes, in the same order, as the Azure Function's cloud table: ".crm9.dynamics.com"
// also ends in ".dynamics.com", so the more specific suffixes are tested first.
const DATAVERSE_CLOUDS: ReadonlyArray<{ suffix: string; cloud: DataverseCloudName }> = [
    { suffix: ".crm.microsoftdynamics.us", cloud: "GCCHigh" },
    { suffix: ".crm.appsplatform.us", cloud: "DoD" },
    { suffix: ".dynamics.cn", cloud: "China" },
    { suffix: ".crm9.dynamics.com", cloud: "GCC" },
    { suffix: ".dynamics.com", cloud: "Commercial" }
];

export class MppUploader implements ComponentFramework.StandardControl<IInputs, IOutputs> {
    private context!: ComponentFramework.Context<IInputs>;
    private container!: HTMLDivElement;
    private root!: HTMLElement;
    private headerDescription!: HTMLParagraphElement;
    private mppModeRadio!: HTMLInputElement;
    private excelModeRadio!: HTMLInputElement;
    private projectModeRadio!: HTMLInputElement;
    private projectOnlineModeRadio!: HTMLInputElement;
    private projectSearchInput!: HTMLInputElement;
    private projectResults!: HTMLDivElement;
    private destinationProjectSection!: HTMLElement;
    private destinationProjectStepNumber!: HTMLSpanElement;
    private selectedProjectPanel!: HTMLDivElement;
    private selectedProjectName!: HTMLSpanElement;
    private clearProjectButton!: HTMLButtonElement;
    private sourceProjectSection!: HTMLElement;
    private sourceProjectStepNumber!: HTMLSpanElement;
    private sourceProjectSearchInput!: HTMLInputElement;
    private sourceProjectSelectAllCheckbox!: HTMLInputElement;
    private sourceProjectSelectionCount!: HTMLSpanElement;
    private sourceProjectGridBody!: HTMLTableSectionElement;
    private sourceProjectGridMessage!: HTMLDivElement;
    private destinationEnvironmentSection!: HTMLElement;
    private destinationEnvironmentStepNumber!: HTMLSpanElement;
    private destinationEnvironmentSelect!: HTMLSelectElement;
    private refreshEnvironmentsButton!: HTMLButtonElement;
    private destinationEnvironmentMessage!: HTMLDivElement;
    private copyResults!: HTMLUListElement;
    private fileSection!: HTMLElement;
    private fileSectionTitle!: HTMLHeadingElement;
    private fileSectionHint!: HTMLSpanElement;
    private dropZone!: HTMLDivElement;
    private dropTitle!: HTMLSpanElement;
    private fileInput!: HTMLInputElement;
    private selectFileButton!: HTMLButtonElement;
    private filePanel!: HTMLDivElement;
    private fileBadge!: HTMLSpanElement;
    private fileNameElement!: HTMLSpanElement;
    private fileSizeElement!: HTMLSpanElement;
    private clearFileButton!: HTMLButtonElement;
    private uploadButton!: HTMLButtonElement;
    private testConnectionButton!: HTMLButtonElement;
    private testConnectionResult!: HTMLDivElement;
    private statusElement!: HTMLDivElement;
    private targetEnvironmentElement!: HTMLParagraphElement;
    private operationDetails!: HTMLDivElement;
    private progressContainer!: HTMLDivElement;
    private progressBar!: HTMLDivElement;
    private progressLabel!: HTMLSpanElement;
    private actionSection!: HTMLElement;
    private pwaSection!: HTMLElement;
    private pwaUrlInput!: HTMLInputElement;
    private pwaSaveUrlButton!: HTMLButtonElement;
    private pwaConnectButton!: HTMLButtonElement;
    private pwaRefreshButton!: HTMLButtonElement;
    private pwaConnectionIndicator!: HTMLSpanElement;
    private pwaConnectionLabel!: HTMLSpanElement;
    private pwaConnectionHint!: HTMLSpanElement;
    private pwaSelectAllCheckbox!: HTMLInputElement;
    private pwaSelectionCount!: HTMLSpanElement;
    private pwaGridBody!: HTMLTableSectionElement;
    private pwaGridMessage!: HTMLDivElement;

    private selectedProject: ProjectOption | null = null;
    private selectedSourceProjects = new Map<string, SourceProjectRow>();
    private visibleSourceProjects: SourceProjectRow[] = [];
    private destinationEnvironments: EnvironmentTarget[] = [];
    private selectedDestinationEnvironment: EnvironmentTarget | null = null;
    private environmentsLoaded = false;
    private isLoadingEnvironments = false;
    private isLoadingSourceProjects = false;
    private environmentsController: AbortController | null = null;
    private isDisposed = false;
    private selectedFile: File | null = null;
    private projectOptions: ProjectOption[] = [];
    private highlightedProjectIndex = -1;
    private importMode: ImportMode = "mpp";
    private state: ControlState = "idle";
    private isDisabled = false;
    private searchTimer: number | null = null;
    private searchSequence = 0;
    private sourceSearchTimer: number | null = null;
    private sourceSearchSequence = 0;
    private activeRequest: XMLHttpRequest | null = null;
    private pollingController: AbortController | null = null;
    private testConnectionController: AbortController | null = null;
    private testConnectionResultTimer: number | null = null;
    private isTestingConnection = false;
    private pwaSharePointUrl = "";
    private persistedPwaSharePointUrl = "";
    private pwaConfigurationId: string | null = null;
    private pwaUrlDirty = false;
    private isSavingPwaUrl = false;
    private isOpeningPwaConnection = false;
    private pwaConnectionState: PwaConnectionState = "notConfigured";
    private pwaProjects: PwaProject[] = [];
    private selectedPwaProjectIds = new Set<string>();
    private expandedPwaProjectIds = new Set<string>();
    private pwaConfigurationLoaded = false;
    private isLoadingPwaConfiguration = false;
    private isLoadingPwaProjects = false;
    private loadingPwaTaskIds = new Set<string>();
    private pwaRequestControllers = new Set<AbortController>();

    public init(
        context: ComponentFramework.Context<IInputs>,
        _notifyOutputChanged: () => void,
        _state: ComponentFramework.Dictionary,
        container: HTMLDivElement
    ): void {
        this.context = context;
        this.container = container;
        this.container.classList.add("mpp-uploader-host");
        this.render();
        this.updateModeView();
        void this.loadPwaConfiguration();
    }

    public updateView(context: ComponentFramework.Context<IInputs>): void {
        this.context = context;
        this.isDisabled = context.mode.isControlDisabled;
        this.applyDisabledState();
    }

    public getOutputs(): IOutputs {
        return {};
    }

    public destroy(): void {
        this.isDisposed = true;
        if (this.searchTimer !== null) {
            window.clearTimeout(this.searchTimer);
        }
        if (this.sourceSearchTimer !== null) {
            window.clearTimeout(this.sourceSearchTimer);
        }
        if (this.testConnectionResultTimer !== null) {
            window.clearTimeout(this.testConnectionResultTimer);
        }
        this.activeRequest?.abort();
        this.pollingController?.abort();
        this.environmentsController?.abort();
        this.testConnectionController?.abort();
        this.pwaRequestControllers.forEach((controller) => controller.abort());
        this.pwaRequestControllers.clear();
        document.removeEventListener("click", this.onDocumentClick);
    }

    private render(): void {
        this.root = document.createElement("section");
        this.root.className = "mpp-uploader";
        this.root.setAttribute("aria-label", "Import Microsoft Project plan");

        const header = document.createElement("header");
        header.className = "mpp-uploader__header";

        const headerIcon = document.createElement("div");
        headerIcon.className = "mpp-uploader__header-icon";
        headerIcon.setAttribute("aria-hidden", "true");
        headerIcon.textContent = "P";

        const headerText = document.createElement("div");
        const title = document.createElement("h2");
        title.className = "mpp-uploader__title";
        title.textContent = "Import project plan";

        this.headerDescription = document.createElement("p");
        this.headerDescription.className = "mpp-uploader__description";
        this.headerDescription.textContent = "Select how you want to import tasks into the destination project.";

        headerText.append(title, this.headerDescription);
        header.append(headerIcon, headerText);

        const content = document.createElement("div");
        content.className = "mpp-uploader__content";
        content.append(
            this.createImportModeSection(),
            this.createPwaSection(),
            this.createSourceProjectSection(),
            this.createProjectSection(),
            this.createDestinationEnvironmentSection(),
            this.createFileSection(),
            this.createActionSection()
        );

        this.root.append(header, content);
        this.container.appendChild(this.root);
        document.addEventListener("click", this.onDocumentClick);
    }

    private createImportModeSection(): HTMLElement {
        const section = document.createElement("section");
        section.className = "mpp-uploader__section mpp-uploader__mode-section";
        section.append(this.createStepHeading(
            "1",
            "Import source",
            "Choose whether the plan comes from an MPP file, an Excel file, another PSA project, or Project Online."
        ));

        const group = document.createElement("div");
        group.className = "mpp-uploader__mode-options";
        group.setAttribute("role", "radiogroup");
        group.setAttribute("aria-label", "Import source");

        const mppOption = this.createModeOption(
            "mpp",
            "Import from .MPP file",
            "Upload a Microsoft Project plan from your device."
        );
        this.mppModeRadio = mppOption.input;

        const projectOption = this.createModeOption(
            "project",
            "Import from Project",
            "Copy tasks from another active PSA project."
        );
        this.projectModeRadio = projectOption.input;

        const excelOption = this.createModeOption(
            "excel",
            "Import from Excel",
            "Upload an Excel project plan from your device."
        );
        this.excelModeRadio = excelOption.input;

        const projectOnlineOption = this.createModeOption(
            "projectOnline",
            "Import from Project Online",
            "Browse projects available in the configured PWA site."
        );
        this.projectOnlineModeRadio = projectOnlineOption.input;

        group.append(
            mppOption.label,
            excelOption.label,
            projectOption.label,
            projectOnlineOption.label
        );
        section.appendChild(group);
        return section;
    }

    private createModeOption(
        mode: ImportMode,
        titleText: string,
        descriptionText: string
    ): { label: HTMLLabelElement; input: HTMLInputElement } {
        const label = document.createElement("label");
        label.className = "mpp-uploader__mode-option";

        const input = document.createElement("input");
        input.className = "mpp-uploader__mode-radio";
        input.type = "radio";
        input.name = "mpp-import-source";
        input.value = mode;
        input.checked = mode === this.importMode;
        input.addEventListener("change", () => {
            if (input.checked) this.setImportMode(mode);
        });

        const indicator = document.createElement("span");
        indicator.className = "mpp-uploader__mode-indicator";
        indicator.setAttribute("aria-hidden", "true");

        const text = document.createElement("span");
        text.className = "mpp-uploader__mode-text";
        const title = document.createElement("span");
        title.className = "mpp-uploader__mode-title";
        title.textContent = titleText;
        const description = document.createElement("span");
        description.className = "mpp-uploader__mode-description";
        description.textContent = descriptionText;
        text.append(title, description);

        label.append(input, indicator, text);
        return { label, input };
    }

    private createPwaSection(): HTMLElement {
        this.pwaSection = document.createElement("section");
        this.pwaSection.className = "mpp-uploader__section mpp-uploader__pwa-section";
        this.pwaSection.append(this.createStepHeading(
            "2",
            "Project Online projects",
            "Enter the PWA URL, connect your Microsoft account, and select the projects to migrate."
        ));

        const urlField = document.createElement("div");
        urlField.className = "mpp-uploader__field";
        const urlLabel = document.createElement("label");
        urlLabel.className = "mpp-uploader__field-label";
        urlLabel.htmlFor = "mpp-pwa-url";
        urlLabel.textContent = "Project Web App URL";

        const urlRow = document.createElement("div");
        urlRow.className = "mpp-uploader__pwa-url-row";
        this.pwaUrlInput = document.createElement("input");
        this.pwaUrlInput.id = "mpp-pwa-url";
        this.pwaUrlInput.className = "mpp-uploader__pwa-url-input";
        this.pwaUrlInput.type = "url";
        this.pwaUrlInput.spellcheck = false;
        this.pwaUrlInput.placeholder = "Loading the configured URL…";
        this.pwaUrlInput.addEventListener("input", this.onPwaUrlInput);

        this.pwaSaveUrlButton = document.createElement("button");
        this.pwaSaveUrlButton.className =
            "mpp-uploader__button mpp-uploader__button--secondary mpp-uploader__pwa-save";
        this.pwaSaveUrlButton.type = "button";
        this.pwaSaveUrlButton.textContent = "Save URL";
        this.pwaSaveUrlButton.addEventListener("click", () => void this.savePwaUrl());
        urlRow.append(this.pwaUrlInput, this.pwaSaveUrlButton);

        const urlHint = document.createElement("span");
        urlHint.className = "mpp-uploader__field-hint";
        urlHint.textContent =
            "Example: https://contoso.sharepoint.com/sites/pwa. Credentials are never stored in this field.";
        urlField.append(urlLabel, urlRow, urlHint);

        const connectionPanel = document.createElement("div");
        connectionPanel.className = "mpp-uploader__pwa-connection-panel";

        const connectionStatus = document.createElement("div");
        connectionStatus.className = "mpp-uploader__pwa-connection-status";
        this.pwaConnectionIndicator = document.createElement("span");
        this.pwaConnectionIndicator.className = "mpp-uploader__pwa-connection-indicator";
        this.pwaConnectionIndicator.setAttribute("aria-hidden", "true");

        const connectionText = document.createElement("div");
        connectionText.className = "mpp-uploader__pwa-connection-text";
        this.pwaConnectionLabel = document.createElement("span");
        this.pwaConnectionLabel.className = "mpp-uploader__pwa-connection-label";
        this.pwaConnectionHint = document.createElement("span");
        this.pwaConnectionHint.className = "mpp-uploader__pwa-connection-hint";
        connectionText.append(this.pwaConnectionLabel, this.pwaConnectionHint);
        connectionStatus.append(this.pwaConnectionIndicator, connectionText);

        const connectionActions = document.createElement("div");
        connectionActions.className = "mpp-uploader__pwa-connection-actions";

        this.pwaConnectButton = document.createElement("button");
        this.pwaConnectButton.className =
            "mpp-uploader__button mpp-uploader__button--secondary mpp-uploader__pwa-connect";
        this.pwaConnectButton.type = "button";
        this.pwaConnectButton.textContent = "Connect to Project Online";
        this.pwaConnectButton.addEventListener("click", () => void this.openPwaConnectionPage());

        this.pwaRefreshButton = document.createElement("button");
        this.pwaRefreshButton.className =
            "mpp-uploader__button mpp-uploader__button--primary mpp-uploader__pwa-refresh";
        this.pwaRefreshButton.type = "button";
        this.pwaRefreshButton.textContent = "Load projects";
        this.pwaRefreshButton.addEventListener("click", () => void this.loadPwaProjects());
        connectionActions.append(this.pwaConnectButton, this.pwaRefreshButton);
        connectionPanel.append(connectionStatus, connectionActions);

        const gridContainer = document.createElement("div");
        gridContainer.className = "mpp-uploader__pwa-grid-container";
        const toolbar = document.createElement("div");
        toolbar.className = "mpp-uploader__pwa-toolbar";
        const selectAllLabel = document.createElement("label");
        selectAllLabel.className = "mpp-uploader__checkbox-label";
        this.pwaSelectAllCheckbox = document.createElement("input");
        this.pwaSelectAllCheckbox.className = "mpp-uploader__checkbox";
        this.pwaSelectAllCheckbox.type = "checkbox";
        this.pwaSelectAllCheckbox.addEventListener("change", this.onPwaSelectAllChange);
        const selectAllText = document.createElement("span");
        selectAllText.textContent = "Select all";
        selectAllLabel.append(this.pwaSelectAllCheckbox, selectAllText);
        this.pwaSelectionCount = document.createElement("span");
        this.pwaSelectionCount.className = "mpp-uploader__pwa-selection-count";
        toolbar.append(selectAllLabel, this.pwaSelectionCount);

        const tableWrapper = document.createElement("div");
        tableWrapper.className = "mpp-uploader__pwa-table-wrapper";
        const table = document.createElement("table");
        table.className = "mpp-uploader__pwa-table";
        const tableHead = document.createElement("thead");
        const headerRow = document.createElement("tr");
        ["Select", "", "Project name", "Start date", "Finish date", "Owner"].forEach((labelText) => {
            const cell = document.createElement("th");
            cell.scope = "col";
            cell.textContent = labelText;
            if (!labelText) cell.setAttribute("aria-label", "Expand tasks");
            headerRow.appendChild(cell);
        });
        tableHead.appendChild(headerRow);
        this.pwaGridBody = document.createElement("tbody");
        table.append(tableHead, this.pwaGridBody);
        tableWrapper.appendChild(table);

        this.pwaGridMessage = document.createElement("div");
        this.pwaGridMessage.className = "mpp-uploader__pwa-message";
        this.pwaGridMessage.setAttribute("role", "status");
        this.pwaGridMessage.setAttribute("aria-live", "polite");

        gridContainer.append(toolbar, tableWrapper, this.pwaGridMessage);
        this.pwaSection.append(urlField, connectionPanel, gridContainer);
        this.setPwaConnectionState(
            "notConfigured",
            "PWA URL required",
            "Enter and save a Project Web App URL to continue."
        );
        this.renderPwaProjects();
        return this.pwaSection;
    }

    private createProjectSection(): HTMLElement {
        this.destinationProjectSection = document.createElement("section");
        this.destinationProjectSection.className = "mpp-uploader__section";
        const heading = this.createStepHeading(
            "2",
            "Destination project",
            "Search for the project that will receive the tasks."
        );
        this.destinationProjectStepNumber = heading.querySelector(".mpp-uploader__step-number") as HTMLSpanElement;
        this.destinationProjectSection.append(heading);

        const searchWrapper = document.createElement("div");
        searchWrapper.className = "mpp-uploader__search";

        const searchIcon = document.createElement("span");
        searchIcon.className = "mpp-uploader__search-icon";
        searchIcon.setAttribute("aria-hidden", "true");
        searchIcon.textContent = "⌕";

        this.projectSearchInput = document.createElement("input");
        this.projectSearchInput.className = "mpp-uploader__search-input";
        this.projectSearchInput.type = "search";
        this.projectSearchInput.placeholder = "Enter at least 2 characters…";
        this.projectSearchInput.autocomplete = "off";
        this.projectSearchInput.setAttribute("role", "combobox");
        this.projectSearchInput.setAttribute("aria-autocomplete", "list");
        this.projectSearchInput.setAttribute("aria-expanded", "false");
        this.projectSearchInput.setAttribute("aria-controls", "mpp-project-results");

        this.projectResults = document.createElement("div");
        this.projectResults.id = "mpp-project-results";
        this.projectResults.className = "mpp-uploader__results";
        this.projectResults.setAttribute("role", "listbox");
        this.projectResults.hidden = true;
        searchWrapper.append(searchIcon, this.projectSearchInput, this.projectResults);

        this.selectedProjectPanel = document.createElement("div");
        this.selectedProjectPanel.className = "mpp-uploader__selection-card";
        this.selectedProjectPanel.hidden = true;

        const selectedIcon = document.createElement("span");
        selectedIcon.className = "mpp-uploader__selection-icon";
        selectedIcon.setAttribute("aria-hidden", "true");
        selectedIcon.textContent = "✓";

        const selectedDetails = document.createElement("div");
        selectedDetails.className = "mpp-uploader__selection-details";
        const selectedLabel = document.createElement("span");
        selectedLabel.className = "mpp-uploader__selection-label";
        selectedLabel.textContent = "Selected project";
        this.selectedProjectName = document.createElement("span");
        this.selectedProjectName.className = "mpp-uploader__selection-name";
        selectedDetails.append(selectedLabel, this.selectedProjectName);

        this.clearProjectButton = document.createElement("button");
        this.clearProjectButton.className = "mpp-uploader__text-button";
        this.clearProjectButton.type = "button";
        this.clearProjectButton.textContent = "Change";
        this.clearProjectButton.addEventListener("click", this.onClearProjectClick);

        this.selectedProjectPanel.append(selectedIcon, selectedDetails, this.clearProjectButton);
        this.destinationProjectSection.append(searchWrapper, this.selectedProjectPanel);

        this.projectSearchInput.addEventListener("input", this.onProjectSearchInput);
        this.projectSearchInput.addEventListener("keydown", this.onProjectSearchKeyDown);
        this.projectSearchInput.addEventListener("focus", this.onProjectSearchFocus);
        return this.destinationProjectSection;
    }

    private createSourceProjectSection(): HTMLElement {
        this.sourceProjectSection = document.createElement("section");
        this.sourceProjectSection.className = "mpp-uploader__section";
        const heading = this.createStepHeading(
            "2",
            "Source projects",
            "Select one or more active projects of this environment to copy."
        );
        this.sourceProjectStepNumber = heading.querySelector(".mpp-uploader__step-number") as HTMLSpanElement;
        this.sourceProjectSection.append(heading);

        const searchWrapper = document.createElement("div");
        searchWrapper.className = "mpp-uploader__search mpp-uploader__source-search";
        const searchIcon = document.createElement("span");
        searchIcon.className = "mpp-uploader__search-icon";
        searchIcon.setAttribute("aria-hidden", "true");
        searchIcon.textContent = "⌕";
        this.sourceProjectSearchInput = document.createElement("input");
        this.sourceProjectSearchInput.className = "mpp-uploader__search-input";
        this.sourceProjectSearchInput.type = "search";
        this.sourceProjectSearchInput.placeholder = "Filter projects by name…";
        this.sourceProjectSearchInput.autocomplete = "off";
        this.sourceProjectSearchInput.setAttribute("aria-label", "Filter source projects by name");
        this.sourceProjectSearchInput.addEventListener("input", this.onSourceProjectSearchInput);
        searchWrapper.append(searchIcon, this.sourceProjectSearchInput);

        const gridContainer = document.createElement("div");
        gridContainer.className = "mpp-uploader__pwa-grid-container";

        const toolbar = document.createElement("div");
        toolbar.className = "mpp-uploader__pwa-toolbar";
        const selectAllLabel = document.createElement("label");
        selectAllLabel.className = "mpp-uploader__checkbox-label";
        this.sourceProjectSelectAllCheckbox = document.createElement("input");
        this.sourceProjectSelectAllCheckbox.className = "mpp-uploader__checkbox";
        this.sourceProjectSelectAllCheckbox.type = "checkbox";
        this.sourceProjectSelectAllCheckbox.addEventListener("change", this.onSourceProjectSelectAllChange);
        const selectAllText = document.createElement("span");
        selectAllText.textContent = "Select all";
        selectAllLabel.append(this.sourceProjectSelectAllCheckbox, selectAllText);
        this.sourceProjectSelectionCount = document.createElement("span");
        this.sourceProjectSelectionCount.className = "mpp-uploader__pwa-selection-count";
        toolbar.append(selectAllLabel, this.sourceProjectSelectionCount);

        const tableWrapper = document.createElement("div");
        tableWrapper.className = "mpp-uploader__pwa-table-wrapper";
        const table = document.createElement("table");
        table.className = "mpp-uploader__pwa-table mpp-uploader__source-table";
        const tableHead = document.createElement("thead");
        const headerRow = document.createElement("tr");
        ["Select", "Project name", "Start date", "Finish date", "Owner"].forEach((labelText) => {
            const cell = document.createElement("th");
            cell.scope = "col";
            cell.textContent = labelText;
            headerRow.appendChild(cell);
        });
        tableHead.appendChild(headerRow);
        this.sourceProjectGridBody = document.createElement("tbody");
        table.append(tableHead, this.sourceProjectGridBody);
        tableWrapper.appendChild(table);

        this.sourceProjectGridMessage = document.createElement("div");
        this.sourceProjectGridMessage.className = "mpp-uploader__pwa-message";
        this.sourceProjectGridMessage.setAttribute("role", "status");
        this.sourceProjectGridMessage.setAttribute("aria-live", "polite");

        gridContainer.append(toolbar, tableWrapper, this.sourceProjectGridMessage);
        this.sourceProjectSection.append(searchWrapper, gridContainer);
        this.renderSourceProjects();
        return this.sourceProjectSection;
    }

    private createDestinationEnvironmentSection(): HTMLElement {
        this.destinationEnvironmentSection = document.createElement("section");
        this.destinationEnvironmentSection.className = "mpp-uploader__section";
        const heading = this.createStepHeading(
            "3",
            "Destination environment",
            "The projects are copied from the current environment into the one you choose."
        );
        this.destinationEnvironmentStepNumber =
            heading.querySelector(".mpp-uploader__step-number") as HTMLSpanElement;
        this.destinationEnvironmentSection.append(heading);

        const field = document.createElement("div");
        field.className = "mpp-uploader__field";
        const label = document.createElement("label");
        label.className = "mpp-uploader__field-label";
        label.htmlFor = "mpp-destination-environment";
        label.textContent = "Destination environment";

        const row = document.createElement("div");
        row.className = "mpp-uploader__pwa-url-row";
        this.destinationEnvironmentSelect = document.createElement("select");
        this.destinationEnvironmentSelect.id = "mpp-destination-environment";
        this.destinationEnvironmentSelect.className =
            "mpp-uploader__pwa-url-input mpp-uploader__environment-select";
        this.destinationEnvironmentSelect.addEventListener("change", this.onDestinationEnvironmentChange);

        this.refreshEnvironmentsButton = document.createElement("button");
        this.refreshEnvironmentsButton.className =
            "mpp-uploader__button mpp-uploader__button--secondary mpp-uploader__pwa-save";
        this.refreshEnvironmentsButton.type = "button";
        this.refreshEnvironmentsButton.textContent = "Refresh";
        this.refreshEnvironmentsButton.addEventListener("click", () => void this.loadDestinationEnvironments(true));
        row.append(this.destinationEnvironmentSelect, this.refreshEnvironmentsButton);

        this.destinationEnvironmentMessage = document.createElement("div");
        this.destinationEnvironmentMessage.className = "mpp-uploader__field-hint";
        this.destinationEnvironmentMessage.setAttribute("role", "status");
        this.destinationEnvironmentMessage.setAttribute("aria-live", "polite");

        field.append(label, row, this.destinationEnvironmentMessage);
        this.destinationEnvironmentSection.append(field);
        this.renderDestinationEnvironments();
        return this.destinationEnvironmentSection;
    }

    private createFileSection(): HTMLElement {
        this.fileSection = document.createElement("section");
        this.fileSection.className = "mpp-uploader__section";
        const heading = this.createStepHeading("3", "Microsoft Project file", "Supported format: .mpp");
        this.fileSectionTitle = heading.querySelector(".mpp-uploader__step-title") as HTMLHeadingElement;
        this.fileSectionHint = heading.querySelector(".mpp-uploader__step-hint") as HTMLSpanElement;
        this.fileSection.append(heading);

        this.dropZone = document.createElement("div");
        this.dropZone.className = "mpp-uploader__drop-zone";
        this.dropZone.tabIndex = 0;
        this.dropZone.setAttribute("role", "button");
        this.dropZone.setAttribute("aria-label", "Select MPP file");

        const uploadIcon = document.createElement("div");
        uploadIcon.className = "mpp-uploader__upload-icon";
        uploadIcon.setAttribute("aria-hidden", "true");
        uploadIcon.textContent = "⇧";
        this.dropTitle = document.createElement("span");
        this.dropTitle.className = "mpp-uploader__drop-title";
        this.dropTitle.textContent = "Drag and drop the .mpp file here";
        const dropHint = document.createElement("span");
        dropHint.className = "mpp-uploader__drop-hint";
        dropHint.textContent = "or browse for it on your device";

        this.selectFileButton = document.createElement("button");
        this.selectFileButton.className = "mpp-uploader__button mpp-uploader__button--secondary";
        this.selectFileButton.type = "button";
        this.selectFileButton.textContent = "Select file";

        this.fileInput = document.createElement("input");
        this.fileInput.className = "mpp-uploader__file-input";
        this.fileInput.type = "file";
        this.fileInput.accept = ".mpp,application/vnd.ms-project";
        this.fileInput.tabIndex = -1;
        this.dropZone.append(uploadIcon, this.dropTitle, dropHint, this.selectFileButton, this.fileInput);

        this.filePanel = document.createElement("div");
        this.filePanel.className = "mpp-uploader__selection-card mpp-uploader__file-card";
        this.filePanel.hidden = true;
        this.fileBadge = document.createElement("span");
        this.fileBadge.className = "mpp-uploader__file-badge";
        this.fileBadge.textContent = "MPP";
        const fileDetails = document.createElement("div");
        fileDetails.className = "mpp-uploader__selection-details";
        this.fileNameElement = document.createElement("span");
        this.fileNameElement.className = "mpp-uploader__selection-name";
        this.fileSizeElement = document.createElement("span");
        this.fileSizeElement.className = "mpp-uploader__selection-label";
        fileDetails.append(this.fileNameElement, this.fileSizeElement);
        this.clearFileButton = document.createElement("button");
        this.clearFileButton.className = "mpp-uploader__text-button";
        this.clearFileButton.type = "button";
        this.clearFileButton.textContent = "Remove";
        this.filePanel.append(this.fileBadge, fileDetails, this.clearFileButton);
        this.fileSection.append(this.dropZone, this.filePanel);

        this.fileInput.addEventListener("change", this.onFileInputChange);
        this.selectFileButton.addEventListener("click", this.onSelectFileClick);
        this.clearFileButton.addEventListener("click", this.onClearFileClick);
        this.dropZone.addEventListener("click", this.onDropZoneClick);
        this.dropZone.addEventListener("keydown", this.onDropZoneKeyDown);
        this.dropZone.addEventListener("dragover", this.onDragOver);
        this.dropZone.addEventListener("dragleave", this.onDragLeave);
        this.dropZone.addEventListener("drop", this.onDrop);
        return this.fileSection;
    }

    private createActionSection(): HTMLElement {
        this.actionSection = document.createElement("section");
        this.actionSection.className = "mpp-uploader__action";
        this.targetEnvironmentElement = document.createElement("p");
        this.renderTargetEnvironment();

        this.uploadButton = document.createElement("button");
        this.uploadButton.className = "mpp-uploader__button mpp-uploader__button--primary";
        this.uploadButton.type = "button";
        this.uploadButton.textContent = "Import tasks";
        this.uploadButton.disabled = true;
        this.uploadButton.addEventListener("click", this.onUploadClick);

        this.progressContainer = document.createElement("div");
        this.progressContainer.className = "mpp-uploader__progress";
        this.progressContainer.hidden = true;
        const progressTrack = document.createElement("div");
        progressTrack.className = "mpp-uploader__progress-track";
        progressTrack.setAttribute("role", "progressbar");
        progressTrack.setAttribute("aria-valuemin", "0");
        progressTrack.setAttribute("aria-valuemax", "100");
        this.progressBar = document.createElement("div");
        this.progressBar.className = "mpp-uploader__progress-bar";
        this.progressLabel = document.createElement("span");
        this.progressLabel.className = "mpp-uploader__progress-label";
        progressTrack.appendChild(this.progressBar);
        this.progressContainer.append(progressTrack, this.progressLabel);

        this.statusElement = document.createElement("div");
        this.statusElement.className = "mpp-uploader__status";
        this.statusElement.setAttribute("role", "status");
        this.statusElement.setAttribute("aria-live", "polite");

        this.operationDetails = document.createElement("div");
        this.operationDetails.className = "mpp-uploader__operation-details";
        this.operationDetails.hidden = true;

        this.copyResults = document.createElement("ul");
        this.copyResults.className = "mpp-uploader__copy-results";
        this.copyResults.setAttribute("aria-label", "Project copy results");
        this.copyResults.hidden = true;

        const connectionTest = document.createElement("div");
        connectionTest.className = "mpp-uploader__connection-test";

        const connectionTestText = document.createElement("div");
        connectionTestText.className = "mpp-uploader__connection-test-text";
        const connectionTestTitle = document.createElement("span");
        connectionTestTitle.className = "mpp-uploader__connection-test-title";
        connectionTestTitle.textContent = "Azure connection";
        const connectionTestHint = document.createElement("span");
        connectionTestHint.className = "mpp-uploader__connection-test-hint";
        connectionTestHint.textContent = "Call the diagnostic endpoint for this environment and display its response.";
        connectionTestText.append(connectionTestTitle, connectionTestHint);

        this.testConnectionButton = document.createElement("button");
        this.testConnectionButton.className =
            "mpp-uploader__button mpp-uploader__button--secondary " +
            "mpp-uploader__test-connection-button";
        this.testConnectionButton.type = "button";
        this.testConnectionButton.textContent = "Test connection";
        this.testConnectionButton.addEventListener("click", this.onTestConnectionClick);

        this.testConnectionResult = document.createElement("div");
        this.testConnectionResult.className = "mpp-uploader__connection-result";
        this.testConnectionResult.setAttribute("role", "status");
        this.testConnectionResult.setAttribute("aria-live", "polite");
        this.testConnectionResult.hidden = true;

        connectionTest.append(
            connectionTestText,
            this.testConnectionButton,
            this.testConnectionResult
        );
        this.actionSection.append(
            this.targetEnvironmentElement,
            this.uploadButton,
            this.progressContainer,
            this.statusElement,
            this.operationDetails,
            this.copyResults,
            connectionTest
        );
        return this.actionSection;
    }

    private createStepHeading(number: string, titleText: string, hintText: string): HTMLElement {
        const heading = document.createElement("div");
        heading.className = "mpp-uploader__step-heading";
        const numberElement = document.createElement("span");
        numberElement.className = "mpp-uploader__step-number";
        numberElement.textContent = number;
        const text = document.createElement("div");
        const title = document.createElement("h3");
        title.className = "mpp-uploader__step-title";
        title.textContent = titleText;
        const hint = document.createElement("span");
        hint.className = "mpp-uploader__step-hint";
        hint.textContent = hintText;
        text.append(title, hint);
        heading.append(numberElement, text);
        return heading;
    }

    private async loadPwaConfiguration(): Promise<void> {
        if (this.isLoadingPwaConfiguration) return;
        this.isLoadingPwaConfiguration = true;
        this.pwaConfigurationId = null;
        this.pwaUrlInput.value = "";
        this.pwaUrlInput.placeholder = "Loading the configured URL…";
        this.showPwaMessage("Loading Project Online configuration…", "loading");
        this.applyDisabledState();

        try {
            const options = `?$select=${PO_COPY_PROJECT_CONFIGURATION_ID_FIELD},${PWA_SHAREPOINT_URL_FIELD}` +
                "&$filter=statecode eq 0&$orderby=createdon asc&$top=1";
            const response = await this.context.webAPI.retrieveMultipleRecords(
                PO_COPY_PROJECT_CONFIGURATION_ENTITY,
                options,
                1
            );
            const configuration = response.entities[0];
            if (!configuration) {
                throw new Error("No active PO Copy Project Configuration record was found.");
            }

            const configurationId = String(
                configuration[PO_COPY_PROJECT_CONFIGURATION_ID_FIELD] ?? ""
            ).replace(/[{}]/g, "");
            if (!configurationId) {
                throw new Error("The PO Copy Project Configuration record ID could not be determined.");
            }

            const url = String(configuration[PWA_SHAREPOINT_URL_FIELD] ?? "").trim();
            this.pwaConfigurationId = configurationId;
            this.pwaSharePointUrl = url;
            this.persistedPwaSharePointUrl = url;
            this.pwaUrlDirty = false;
            this.pwaUrlInput.value = url;
            this.pwaUrlInput.title = url;
            this.pwaUrlInput.placeholder = "https://contoso.sharepoint.com/sites/pwa";
            if (url && !this.isValidPwaUrl(url)) {
                this.setPwaConnectionState(
                    "error",
                    "Invalid PWA URL",
                    "Enter a valid HTTPS Project Web App URL and save it."
                );
                this.showPwaMessage(
                    "The configured mfd_posharepointurl value is not a valid HTTPS URL.",
                    "error"
                );
                return;
            }

            if (!url) {
                this.setPwaConnectionState(
                    "notConfigured",
                    "PWA URL required",
                    "Enter and save a Project Web App URL to continue."
                );
                this.showPwaMessage("Enter the Project Web App URL to begin.", "info");
                return;
            }

            this.setPwaConnectionState(
                "notConnected",
                "Not connected",
                "Connect a Microsoft account that can access this PWA site."
            );
            this.showPwaMessage(
                "The Project Web App URL was loaded. Connect to Project Online, then load the projects.",
                "info"
            );
        } catch (error: unknown) {
            this.pwaConfigurationId = null;
            this.pwaSharePointUrl = "";
            this.persistedPwaSharePointUrl = "";
            this.pwaUrlInput.value = "";
            this.pwaUrlInput.placeholder = "https://contoso.sharepoint.com/sites/pwa";
            this.setPwaConnectionState(
                "error",
                "Configuration unavailable",
                "The Project Online configuration record could not be loaded."
            );
            this.showPwaMessage(
                error instanceof Error && error.message
                    ? error.message
                    : "Project Online configuration could not be loaded.",
                "error"
            );
        } finally {
            this.pwaConfigurationLoaded = true;
            this.isLoadingPwaConfiguration = false;
            this.applyDisabledState();
        }
    }

    private readonly onPwaUrlInput = (): void => {
        const value = this.pwaUrlInput.value.trim();
        this.pwaSharePointUrl = value;
        this.pwaUrlDirty = value !== this.persistedPwaSharePointUrl;
        this.clearPwaProjects();

        if (!value) {
            this.setPwaConnectionState(
                "notConfigured",
                "PWA URL required",
                "Enter and save a Project Web App URL to continue."
            );
            this.showPwaMessage("Enter the Project Web App URL to begin.", "info");
        } else if (!this.isValidPwaUrl(value)) {
            this.setPwaConnectionState(
                "error",
                "Invalid PWA URL",
                "Use a complete HTTPS URL without credentials, query parameters, or fragments."
            );
            this.showPwaMessage("Enter a valid HTTPS Project Web App URL.", "error");
        } else if (this.pwaUrlDirty) {
            this.setPwaConnectionState(
                "notConnected",
                "Unsaved URL",
                "Save the URL before connecting to Project Online."
            );
            this.showPwaMessage("Save the PWA URL, then connect your Microsoft account.", "info");
        } else {
            this.setPwaConnectionState(
                "notConnected",
                "Not connected",
                "Connect a Microsoft account that can access this PWA site."
            );
        }
        this.applyDisabledState();
    };

    private async savePwaUrl(showSuccessMessage = true): Promise<boolean> {
        if (this.isSavingPwaUrl) return false;
        const normalizedUrl = this.normalizePwaUrl(this.pwaUrlInput.value);
        if (!normalizedUrl) {
            this.setPwaConnectionState(
                "error",
                "Invalid PWA URL",
                "Use a complete HTTPS URL without credentials, query parameters, or fragments."
            );
            this.showPwaMessage("Enter a valid HTTPS Project Web App URL before saving.", "error");
            this.applyDisabledState();
            return false;
        }
        if (!this.pwaConfigurationId) {
            this.setPwaConnectionState(
                "error",
                "Configuration unavailable",
                "An active PO Copy Project Configuration record is required."
            );
            this.showPwaMessage("The Project Online configuration record is not available.", "error");
            return false;
        }
        if (!this.pwaUrlDirty && normalizedUrl === this.persistedPwaSharePointUrl) return true;

        this.isSavingPwaUrl = true;
        this.pwaSaveUrlButton.textContent = "Saving…";
        this.showPwaMessage("Saving the Project Web App URL…", "loading");
        this.applyDisabledState();

        try {
            await this.context.webAPI.updateRecord(
                PO_COPY_PROJECT_CONFIGURATION_ENTITY,
                this.pwaConfigurationId,
                { [PWA_SHAREPOINT_URL_FIELD]: normalizedUrl }
            );
            this.pwaSharePointUrl = normalizedUrl;
            this.persistedPwaSharePointUrl = normalizedUrl;
            this.pwaUrlDirty = false;
            this.pwaUrlInput.value = normalizedUrl;
            this.pwaUrlInput.title = normalizedUrl;
            this.setPwaConnectionState(
                "notConnected",
                "Not connected",
                "The URL is saved. Connect a Microsoft account that can access this PWA site."
            );
            if (showSuccessMessage) {
                this.showPwaMessage("The Project Web App URL was saved successfully.", "success");
            }
            return true;
        } catch (error: unknown) {
            this.setPwaConnectionState(
                "error",
                "URL could not be saved",
                "Check your permission to update PO Copy Project Configuration."
            );
            this.showPwaMessage(
                error instanceof Error && error.message
                    ? error.message
                    : "The Project Web App URL could not be saved.",
                "error"
            );
            return false;
        } finally {
            this.isSavingPwaUrl = false;
            this.pwaSaveUrlButton.textContent = "Save URL";
            this.applyDisabledState();
        }
    }

    private async openPwaConnectionPage(): Promise<void> {
        if (this.isOpeningPwaConnection || !this.isValidPwaUrl(this.pwaUrlInput.value)) return;
        if (this.pwaUrlDirty && !await this.savePwaUrl(false)) return;

        const pageName = (this.context.parameters.pwaConnectionPageName.raw ?? "").trim();
        if (!pageName) {
            this.setPwaConnectionState(
                "error",
                "Connection page unavailable",
                "Configure the Project Online Connection Page Name property on the control."
            );
            this.showPwaMessage(
                "Configure the Project Online Connection Page Name property before connecting.",
                "error"
            );
            return;
        }
        if (!this.pwaConfigurationId) {
            this.showPwaMessage("The Project Online configuration record is not available.", "error");
            return;
        }

        const navigation = this.getXrmNavigation();
        if (!navigation) {
            this.setPwaConnectionState(
                "error",
                "Connection page unavailable",
                "The model-driven app navigation API is not available in this host."
            );
            this.showPwaMessage("The Project Online connection page could not be opened.", "error");
            return;
        }

        this.isOpeningPwaConnection = true;
        this.pwaConnectButton.textContent = "Connecting…";
        this.setPwaConnectionState(
            "connecting",
            "Connecting",
            "Complete Microsoft sign-in in the connection dialog."
        );
        this.showPwaMessage("Complete Microsoft sign-in in the connection dialog.", "loading");
        this.applyDisabledState();

        try {
            await navigation.navigateTo(
                {
                    pageType: "custom",
                    name: pageName,
                    entityName: PO_COPY_PROJECT_CONFIGURATION_ENTITY,
                    recordId: this.pwaConfigurationId
                },
                {
                    target: 2,
                    position: 1,
                    width: { value: 560, unit: "px" },
                    height: { value: 650, unit: "px" },
                    title: "Connect to Project Online"
                }
            );
            this.setPwaConnectionState(
                "ready",
                "Ready to verify",
                "Load the projects to verify that the connection can access this PWA site."
            );
            this.showPwaMessage(
                "The connection dialog was closed. Select Load projects to verify access.",
                "info"
            );
        } catch (error: unknown) {
            this.setPwaConnectionState(
                "error",
                "Connection page failed",
                "The Project Online connection dialog could not be completed."
            );
            this.showPwaMessage(
                error instanceof Error && error.message
                    ? error.message
                    : "The Project Online connection page could not be opened.",
                "error"
            );
        } finally {
            this.isOpeningPwaConnection = false;
            this.pwaConnectButton.textContent = "Connect to Project Online";
            this.applyDisabledState();
        }
    }

    private setPwaConnectionState(
        state: PwaConnectionState,
        label: string,
        hint: string
    ): void {
        this.pwaConnectionState = state;
        if (!this.pwaConnectionIndicator || !this.pwaConnectionLabel || !this.pwaConnectionHint) return;
        this.pwaConnectionIndicator.dataset.state = state;
        this.pwaConnectionLabel.textContent = label;
        this.pwaConnectionHint.textContent = hint;
    }

    private clearPwaProjects(): void {
        this.pwaProjects = [];
        this.selectedPwaProjectIds.clear();
        this.expandedPwaProjectIds.clear();
        this.loadingPwaTaskIds.clear();
        this.renderPwaProjects();
    }

    private normalizePwaUrl(value: string): string | null {
        try {
            const url = new URL(value.trim());
            if (
                url.protocol !== "https:" ||
                !url.hostname ||
                url.username ||
                url.password ||
                url.search ||
                url.hash
            ) {
                return null;
            }
            return url.toString().replace(/\/$/, "");
        } catch {
            return null;
        }
    }

    private isValidPwaUrl(value: string): boolean {
        return this.normalizePwaUrl(value) !== null;
    }

    private getXrmNavigation(): XrmNavigationApi | null {
        const hosts: Window[] = [window];
        try {
            if (window.parent !== window) hosts.push(window.parent);
        } catch {
            return null;
        }

        for (const host of hosts) {
            try {
                const candidate = host as unknown as { Xrm?: { Navigation?: XrmNavigationApi } };
                if (candidate.Xrm?.Navigation?.navigateTo) return candidate.Xrm.Navigation;
            } catch {
                continue;
            }
        }
        return null;
    }

    private async loadPwaProjects(): Promise<void> {
        if (this.isLoadingPwaProjects) return;
        if (!this.isValidPwaUrl(this.pwaUrlInput.value)) {
            this.showPwaMessage("Enter a valid HTTPS Project Web App URL before loading projects.", "error");
            return;
        }
        if (this.pwaUrlDirty && !await this.savePwaUrl(false)) return;

        const endpoint = (this.context.parameters.pwaProjectsFunctionUrl.raw ?? "").trim();
        if (!this.isValidHttpsUrl(endpoint)) {
            this.clearPwaProjects();
            this.showPwaMessage(
                "Configure the Project Online Projects Flow URL property to load PWA projects.",
                "info"
            );
            return;
        }

        this.isLoadingPwaProjects = true;
        this.pwaRefreshButton.textContent = "Loading…";
        this.setPwaConnectionState(
            "connecting",
            "Verifying access",
            "Loading projects from the configured PWA site."
        );
        this.showPwaMessage("Loading projects from Project Online…", "loading");
        this.applyDisabledState();

        try {
            const response = await this.postPwaRequest(endpoint, {
                pwaSiteUrl: this.pwaSharePointUrl,
                configurationId: this.pwaConfigurationId ?? ""
            });
            this.pwaProjects = this.normalizePwaProjects(response);
            const availableIds = new Set(this.pwaProjects.map((project) => project.id));
            this.selectedPwaProjectIds = new Set(
                [...this.selectedPwaProjectIds].filter((id) => availableIds.has(id))
            );
            this.expandedPwaProjectIds = new Set(
                [...this.expandedPwaProjectIds].filter((id) => availableIds.has(id))
            );
            this.renderPwaProjects();
            this.setPwaConnectionState(
                "connected",
                "Connected",
                "The connection can access the configured Project Web App site."
            );
            this.showPwaMessage(
                this.pwaProjects.length === 0
                    ? "No projects were found in the configured PWA site."
                    : `${this.pwaProjects.length} Project Online ${this.pwaProjects.length === 1 ? "project" : "projects"} loaded.`,
                this.pwaProjects.length === 0 ? "info" : "success"
            );
        } catch (error: unknown) {
            this.clearPwaProjects();
            this.setPwaConnectionState(
                "error",
                "Connection failed",
                "Open the connection dialog and verify the account has access to this PWA site."
            );
            this.showPwaMessage(
                error instanceof Error && error.message
                    ? error.message
                    : "Projects could not be loaded from Project Online.",
                "error"
            );
        } finally {
            this.isLoadingPwaProjects = false;
            this.pwaRefreshButton.textContent = this.pwaProjects.length > 0
                ? "Refresh projects"
                : "Load projects";
            this.applyDisabledState();
        }
    }

    private readonly onPwaSelectAllChange = (): void => {
        if (this.pwaSelectAllCheckbox.checked) {
            this.pwaProjects.forEach((project) => this.selectedPwaProjectIds.add(project.id));
        } else {
            this.selectedPwaProjectIds.clear();
        }
        this.renderPwaProjects();
    };

    private setPwaProjectSelected(projectId: string, selected: boolean): void {
        if (selected) this.selectedPwaProjectIds.add(projectId);
        else this.selectedPwaProjectIds.delete(projectId);
        this.updatePwaSelectionSummary();
    }

    private togglePwaProject(projectId: string): void {
        if (this.expandedPwaProjectIds.has(projectId)) {
            this.expandedPwaProjectIds.delete(projectId);
            this.renderPwaProjects();
            return;
        }

        this.expandedPwaProjectIds.add(projectId);
        this.renderPwaProjects();
        const project = this.pwaProjects.find((item) => item.id === projectId);
        if (project && project.tasks === null) void this.loadPwaTasks(projectId);
    }

    private async loadPwaTasks(projectId: string): Promise<void> {
        if (this.loadingPwaTaskIds.has(projectId)) return;
        const endpoint = (this.context.parameters.pwaProjectTasksFunctionUrl.raw ?? "").trim();
        if (!this.isValidHttpsUrl(endpoint)) {
            this.renderPwaProjects();
            return;
        }

        this.loadingPwaTaskIds.add(projectId);
        this.renderPwaProjects();
        try {
            const response = await this.postPwaRequest(endpoint, {
                pwaSiteUrl: this.pwaSharePointUrl,
                projectId,
                configurationId: this.pwaConfigurationId ?? ""
            });
            const project = this.pwaProjects.find((item) => item.id === projectId);
            if (project) project.tasks = this.normalizePwaTasks(response);
        } catch (error: unknown) {
            this.showPwaMessage(
                error instanceof Error && error.message
                    ? error.message
                    : "The project tasks could not be loaded.",
                "error"
            );
        } finally {
            this.loadingPwaTaskIds.delete(projectId);
            this.renderPwaProjects();
        }
    }

    private renderPwaProjects(): void {
        if (!this.pwaGridBody) return;
        this.pwaGridBody.replaceChildren();

        this.pwaProjects.forEach((project) => {
            const row = document.createElement("tr");
            row.className = "mpp-uploader__pwa-project-row";
            if (this.selectedPwaProjectIds.has(project.id)) {
                row.classList.add("mpp-uploader__pwa-project-row--selected");
            }

            const selectCell = document.createElement("td");
            const checkbox = document.createElement("input");
            checkbox.className = "mpp-uploader__checkbox mpp-uploader__pwa-row-checkbox";
            checkbox.type = "checkbox";
            checkbox.checked = this.selectedPwaProjectIds.has(project.id);
            checkbox.setAttribute("aria-label", `Select ${project.name}`);
            checkbox.addEventListener("change", () => {
                this.setPwaProjectSelected(project.id, checkbox.checked);
                row.classList.toggle("mpp-uploader__pwa-project-row--selected", checkbox.checked);
            });
            selectCell.appendChild(checkbox);

            const expandCell = document.createElement("td");
            const expandButton = document.createElement("button");
            const isExpanded = this.expandedPwaProjectIds.has(project.id);
            expandButton.className = "mpp-uploader__pwa-expand";
            expandButton.type = "button";
            expandButton.textContent = isExpanded ? "⌄" : "›";
            expandButton.setAttribute("aria-expanded", String(isExpanded));
            expandButton.setAttribute("aria-label", `${isExpanded ? "Hide" : "Show"} tasks for ${project.name}`);
            expandButton.addEventListener("click", () => this.togglePwaProject(project.id));
            expandCell.appendChild(expandButton);

            const nameCell = document.createElement("td");
            nameCell.className = "mpp-uploader__pwa-project-name";
            nameCell.textContent = project.name;
            nameCell.title = project.name;

            row.append(
                selectCell,
                expandCell,
                nameCell,
                this.createPwaTextCell(this.formatPwaDate(project.startDate)),
                this.createPwaTextCell(this.formatPwaDate(project.finishDate)),
                this.createPwaTextCell(project.ownerName ?? "—")
            );
            this.pwaGridBody.appendChild(row);

            if (isExpanded) this.pwaGridBody.appendChild(this.createPwaTasksRow(project));
        });

        this.updatePwaSelectionSummary();
        this.applyPwaDisabledState();
    }

    private createPwaTasksRow(project: PwaProject): HTMLTableRowElement {
        const row = document.createElement("tr");
        row.className = "mpp-uploader__pwa-tasks-row";
        const cell = document.createElement("td");
        cell.colSpan = 6;
        const content = document.createElement("div");
        content.className = "mpp-uploader__pwa-tasks-content";

        if (this.loadingPwaTaskIds.has(project.id)) {
            content.classList.add("mpp-uploader__pwa-tasks-message--loading");
            content.textContent = "Loading tasks…";
        } else if (project.tasks === null) {
            content.textContent = this.isValidHttpsUrl(
                (this.context.parameters.pwaProjectTasksFunctionUrl.raw ?? "").trim()
            )
                ? "Expand the project again to retry loading its tasks."
                : "Configure the Project Online Tasks Function URL to load this project's tasks.";
        } else if (project.tasks.length === 0) {
            content.textContent = "This project has no tasks.";
        } else {
            const table = document.createElement("table");
            table.className = "mpp-uploader__pwa-task-table";
            const head = document.createElement("thead");
            const headRow = document.createElement("tr");
            ["Task name", "Start date", "Finish date", "Duration"].forEach((labelText) => {
                const heading = document.createElement("th");
                heading.scope = "col";
                heading.textContent = labelText;
                headRow.appendChild(heading);
            });
            head.appendChild(headRow);
            const body = document.createElement("tbody");
            project.tasks.forEach((task) => {
                const taskRow = document.createElement("tr");
                taskRow.append(
                    this.createPwaTextCell(task.name),
                    this.createPwaTextCell(this.formatPwaDate(task.startDate)),
                    this.createPwaTextCell(this.formatPwaDate(task.finishDate)),
                    this.createPwaTextCell(task.duration ?? "—")
                );
                body.appendChild(taskRow);
            });
            table.append(head, body);
            content.appendChild(table);
        }

        cell.appendChild(content);
        row.appendChild(cell);
        return row;
    }

    private createPwaTextCell(value: string): HTMLTableCellElement {
        const cell = document.createElement("td");
        cell.textContent = value;
        cell.title = value === "—" ? "" : value;
        return cell;
    }

    private updatePwaSelectionSummary(): void {
        const selectedCount = this.selectedPwaProjectIds.size;
        const projectCount = this.pwaProjects.length;
        this.pwaSelectionCount.textContent = selectedCount === 0
            ? "No projects selected"
            : `${selectedCount} ${selectedCount === 1 ? "project" : "projects"} selected`;
        this.pwaSelectAllCheckbox.checked = projectCount > 0 && selectedCount === projectCount;
        this.pwaSelectAllCheckbox.indeterminate = selectedCount > 0 && selectedCount < projectCount;
    }

    private showPwaMessage(
        message: string,
        type: "info" | "loading" | "success" | "error"
    ): void {
        this.pwaGridMessage.className = `mpp-uploader__pwa-message mpp-uploader__pwa-message--${type}`;
        this.pwaGridMessage.textContent = message;
    }

    private async postPwaRequest(endpoint: string, payload: Record<string, string>): Promise<unknown> {
        const controller = new AbortController();
        this.pwaRequestControllers.add(controller);
        const timeoutSeconds = this.getPositiveNumber(
            this.context.parameters.requestTimeoutSeconds.raw,
            DEFAULT_TIMEOUT_SECONDS
        );
        const timeoutId = window.setTimeout(() => controller.abort(), timeoutSeconds * 1000);

        try {
            const response = await fetch(endpoint, {
                method: "POST",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(payload),
                signal: controller.signal
            });
            const body = this.parseResponseText(await response.text());
            if (!response.ok) throw new Error(this.extractErrorMessage(body, response.status));
            return body;
        } catch (error: unknown) {
            if (error instanceof DOMException && error.name === "AbortError") {
                throw new Error(`The Project Online request exceeded ${timeoutSeconds} seconds.`);
            }
            throw error;
        } finally {
            window.clearTimeout(timeoutId);
            this.pwaRequestControllers.delete(controller);
        }
    }

    private normalizePwaProjects(response: unknown): PwaProject[] {
        return this.extractPwaArray(response, ["projects", "Projects", "value", "Payload"])
            .map((item): PwaProject | null => {
                const value = this.asObject(item);
                if (!value) return null;
                const id = this.normalizeGuid(this.getFirstPwaString(value, [
                    "ProjectId", "projectId", "id", "Id"
                ]) ?? "");
                const name = this.getFirstPwaString(value, [
                    "ProjectName", "projectName", "name", "Name"
                ]);
                if (!id || !name) return null;
                const inlineTasks = this.getFirstPwaArray(value, ["tasks", "Tasks"]);
                return {
                    id,
                    name,
                    startDate: this.getFirstPwaString(value, [
                        "ProjectStartDate", "projectStartDate", "startDate", "StartDate"
                    ]),
                    finishDate: this.getFirstPwaString(value, [
                        "ProjectFinishDate", "projectFinishDate", "finishDate", "FinishDate"
                    ]),
                    ownerName: this.getFirstPwaString(value, [
                        "ProjectOwnerName", "projectOwnerName", "ownerName", "OwnerName"
                    ]),
                    tasks: inlineTasks ? this.normalizePwaTasks(inlineTasks) : null
                };
            })
            .filter((project): project is PwaProject => project !== null)
            .sort((left, right) => left.name.localeCompare(right.name));
    }

    private normalizePwaTasks(response: unknown): PwaTask[] {
        return this.extractPwaArray(response, ["tasks", "Tasks", "value", "Payload"])
            .map((item): PwaTask | null => {
                const value = this.asObject(item);
                if (!value) return null;
                const idValue = this.getFirstPwaString(value, ["TaskId", "taskId", "id", "Id"]);
                const name = this.getFirstPwaString(value, ["TaskName", "taskName", "name", "Name"]);
                if (!idValue || !name) return null;
                return {
                    id: this.normalizeGuid(idValue) ?? idValue,
                    name,
                    startDate: this.getFirstPwaString(value, [
                        "TaskStartDate", "taskStartDate", "startDate", "StartDate"
                    ]),
                    finishDate: this.getFirstPwaString(value, [
                        "TaskFinishDate", "taskFinishDate", "finishDate", "FinishDate"
                    ]),
                    duration: this.getFirstPwaDisplayValue(value, [
                        "TaskDuration", "taskDuration", "duration", "Duration"
                    ])
                };
            })
            .filter((task): task is PwaTask => task !== null);
    }

    private extractPwaArray(response: unknown, keys: string[]): unknown[] {
        if (Array.isArray(response)) return response;
        const value = this.asObject(response);
        if (!value) return [];
        const direct = this.getFirstPwaArray(value, keys);
        if (direct) return direct;
        const data = this.asObject(value.d);
        if (!data) return [];
        if (Array.isArray(data.results)) return data.results;
        return [];
    }

    private getFirstPwaArray(value: Record<string, unknown>, keys: string[]): unknown[] | null {
        for (const key of keys) {
            if (Array.isArray(value[key])) return value[key] as unknown[];
        }
        return null;
    }

    private getFirstPwaString(value: Record<string, unknown>, keys: string[]): string | null {
        for (const key of keys) {
            const property = value[key];
            if (typeof property === "string" && property.trim()) return property.trim();
        }
        return null;
    }

    private getFirstPwaDisplayValue(value: Record<string, unknown>, keys: string[]): string | null {
        for (const key of keys) {
            const property = value[key];
            if (typeof property === "string" && property.trim()) return property.trim();
            if (typeof property === "number" && Number.isFinite(property)) return String(property);
        }
        return null;
    }

    private formatPwaDate(value: string | null): string {
        if (!value) return "—";
        const verboseDate = /^\/Date\((\d+)(?:[+-]\d+)?\)\/$/.exec(value);
        const timestamp = verboseDate ? Number(verboseDate[1]) : Date.parse(value);
        if (!Number.isFinite(timestamp)) return value;
        return new Intl.DateTimeFormat(undefined, {
            year: "numeric",
            month: "2-digit",
            day: "2-digit"
        }).format(new Date(timestamp));
    }

    private applyPwaDisabledState(): void {
        if (!this.pwaSection) return;
        const disabled = this.isDisabled || this.isBusy();
        const validUrl = this.isValidPwaUrl(this.pwaUrlInput.value);
        const unavailable = disabled || this.isLoadingPwaConfiguration || this.isSavingPwaUrl ||
            this.isOpeningPwaConnection;
        this.pwaUrlInput.disabled = unavailable;
        this.pwaSaveUrlButton.disabled = unavailable || !validUrl || !this.pwaUrlDirty ||
            !this.pwaConfigurationId;
        this.pwaConnectButton.disabled = unavailable || !validUrl || !this.pwaConfigurationId;
        this.pwaRefreshButton.disabled = unavailable || this.isLoadingPwaProjects || !validUrl ||
            !this.pwaConfigurationId || this.pwaConnectionState === "connecting";
        this.pwaSelectAllCheckbox.disabled = disabled || this.isLoadingPwaProjects ||
            this.pwaProjects.length === 0;
        this.pwaSection.querySelectorAll<HTMLInputElement>(".mpp-uploader__pwa-row-checkbox")
            .forEach((checkbox) => { checkbox.disabled = disabled || this.isLoadingPwaProjects; });
        this.pwaSection.querySelectorAll<HTMLButtonElement>(".mpp-uploader__pwa-expand")
            .forEach((button) => { button.disabled = disabled || this.isLoadingPwaProjects; });
    }

    private readonly onDocumentClick = (event: MouseEvent): void => {
        if (!this.root.contains(event.target as Node)) {
            this.closeProjectResults();
        }
    };

    private setImportMode(mode: ImportMode): void {
        if (this.isBusy() || this.importMode === mode) return;
        this.selectedFile = null;
        this.fileInput.value = "";
        this.filePanel.hidden = true;
        this.dropZone.hidden = false;
        this.importMode = mode;
        this.closeProjectResults();
        this.hideProgress();
        this.clearCopyResults();
        this.updateModeView();
        if (mode === "project") {
            if (this.visibleSourceProjects.length === 0 && !this.isLoadingSourceProjects) {
                void this.loadSourceProjects();
            }
            void this.loadDestinationEnvironments();
        }
        if (mode === "projectOnline") {
            if (!this.pwaConfigurationLoaded && !this.isLoadingPwaConfiguration) {
                void this.loadPwaConfiguration();
            }
        }
    }

    private updateModeView(): void {
        const isMppMode = this.importMode === "mpp";
        const isExcelMode = this.importMode === "excel";
        const isProjectMode = this.importMode === "project";
        const isProjectOnlineMode = this.importMode === "projectOnline";
        this.mppModeRadio.checked = isMppMode;
        this.excelModeRadio.checked = isExcelMode;
        this.projectModeRadio.checked = isProjectMode;
        this.projectOnlineModeRadio.checked = isProjectOnlineMode;
        this.pwaSection.hidden = !isProjectOnlineMode;
        this.fileSection.hidden = isProjectMode || isProjectOnlineMode;
        this.sourceProjectSection.hidden = !isProjectMode;
        this.destinationProjectSection.hidden = isProjectOnlineMode || isProjectMode;
        this.destinationEnvironmentSection.hidden = !isProjectMode;
        this.actionSection.hidden = isProjectOnlineMode;
        this.sourceProjectStepNumber.textContent = "2";
        this.destinationEnvironmentStepNumber.textContent = "3";
        this.destinationProjectStepNumber.textContent = "2";
        this.fileSectionTitle.textContent = isExcelMode ? "Excel project file" : "Microsoft Project file";
        this.fileSectionHint.textContent = isExcelMode ? "Supported format: .xlsx" : "Supported format: .mpp";
        this.dropTitle.textContent = isExcelMode
            ? "Drag and drop the .xlsx file here"
            : "Drag and drop the .mpp file here";
        this.dropZone.setAttribute("aria-label", isExcelMode ? "Select Excel file" : "Select MPP file");
        this.fileInput.accept = isExcelMode
            ? ".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : ".mpp,application/vnd.ms-project";
        this.fileBadge.textContent = isExcelMode ? "XLSX" : "MPP";
        this.headerDescription.textContent = isProjectOnlineMode
            ? "Connect to a Project Web App site, then browse and select the projects to migrate."
            : isProjectMode
                ? "Copy one or more active PSA projects from this environment into another environment."
                : isExcelMode
                    ? "Import tasks from an Excel project file into an active PSA project."
                    : "Import tasks from a Microsoft Project file into an active PSA project.";
        this.uploadButton.textContent = isProjectMode ? "Copy project tasks" : "Import tasks";
        this.updateReadyState();
    }

    private readonly onProjectSearchInput = (): void => {
        if (this.selectedProject && this.projectSearchInput.value !== this.selectedProject.name) {
            this.selectedProject = null;
            this.selectedProjectPanel.hidden = true;
        }
        this.scheduleProjectSearch();
        this.updateReadyState();
    };

    private readonly onProjectSearchFocus = (): void => {
        if (!this.selectedProject && this.projectSearchInput.value.trim().length >= PROJECT_SEARCH_MIN_LENGTH) {
            this.scheduleProjectSearch(true);
        }
    };

    private readonly onProjectSearchKeyDown = (event: KeyboardEvent): void => {
        if (this.projectResults.hidden || this.projectOptions.length === 0) {
            if (event.key === "Escape") this.closeProjectResults();
            return;
        }
        if (event.key === "ArrowDown") {
            event.preventDefault();
            this.highlightedProjectIndex = Math.min(this.highlightedProjectIndex + 1, this.projectOptions.length - 1);
            this.updateHighlightedProject();
        } else if (event.key === "ArrowUp") {
            event.preventDefault();
            this.highlightedProjectIndex = Math.max(this.highlightedProjectIndex - 1, 0);
            this.updateHighlightedProject();
        } else if (event.key === "Enter" && this.highlightedProjectIndex >= 0) {
            event.preventDefault();
            this.selectProject(this.projectOptions[this.highlightedProjectIndex]);
        } else if (event.key === "Escape") {
            this.closeProjectResults();
        }
    };

    private readonly onClearProjectClick = (): void => {
        if (this.isBusy()) return;
        this.selectedProject = null;
        this.projectSearchInput.value = "";
        this.selectedProjectPanel.hidden = true;
        this.projectSearchInput.focus();
        this.closeProjectResults();
        this.updateReadyState();
    };

    private scheduleProjectSearch(immediate = false): void {
        if (this.searchTimer !== null) window.clearTimeout(this.searchTimer);
        const query = this.projectSearchInput.value.trim();
        if (query.length < PROJECT_SEARCH_MIN_LENGTH || this.selectedProject) {
            this.closeProjectResults();
            return;
        }
        this.searchTimer = window.setTimeout(() => void this.searchProjects(query), immediate ? 0 : PROJECT_SEARCH_DELAY_MS);
    }

    private async searchProjects(searchText: string): Promise<void> {
        const sequence = ++this.searchSequence;
        this.renderProjectMessage("Searching for projects…", true);
        try {
            const escapedSearch = searchText.replace(/'/g, "''");
            const options = "?$select=msdyn_projectid,msdyn_subject" +
                "&$filter=statecode eq 0 and " + `contains(msdyn_subject,'${escapedSearch}')` +
                "&$orderby=msdyn_subject asc";
            const response = await this.context.webAPI.retrieveMultipleRecords(
                "msdyn_project",
                options,
                PROJECT_RESULT_LIMIT
            );
            if (sequence !== this.searchSequence || this.selectedProject) return;
            this.projectOptions = response.entities
                .map((entity): ProjectOption | null => {
                    const id = this.normalizeGuid(String(entity.msdyn_projectid ?? ""));
                    const name = String(entity.msdyn_subject ?? "").trim();
                    return id && name ? { id, name } : null;
                })
                .filter((project): project is ProjectOption => project !== null);
            this.renderProjectResults();
        } catch (error: unknown) {
            if (sequence !== this.searchSequence) return;
            const detail = error instanceof Error && error.message ? ` ${error.message}` : "";
            this.renderProjectMessage(`Projects could not be retrieved.${detail}`, false);
        }
    }

    private renderProjectResults(): void {
        this.projectResults.replaceChildren();
        this.highlightedProjectIndex = this.projectOptions.length > 0 ? 0 : -1;
        if (this.projectOptions.length === 0) {
            this.renderProjectMessage("No active projects were found.", false);
            return;
        }
        this.projectOptions.forEach((project, index) => {
            const option = document.createElement("button");
            option.className = "mpp-uploader__result";
            option.type = "button";
            option.setAttribute("role", "option");
            option.setAttribute("aria-selected", String(index === this.highlightedProjectIndex));
            const icon = document.createElement("span");
            icon.className = "mpp-uploader__result-icon";
            icon.textContent = "P";
            const name = document.createElement("span");
            name.className = "mpp-uploader__result-name";
            name.textContent = project.name;
            name.title = project.name;
            option.append(icon, name);
            option.addEventListener("mousedown", (event) => event.preventDefault());
            option.addEventListener("click", () => this.selectProject(project));
            this.projectResults.appendChild(option);
        });
        this.projectResults.hidden = false;
        this.projectSearchInput.setAttribute("aria-expanded", "true");
    }

    private renderProjectMessage(message: string, loading: boolean): void {
        this.projectOptions = [];
        this.highlightedProjectIndex = -1;
        this.projectResults.replaceChildren();
        const element = document.createElement("div");
        element.className = loading
            ? "mpp-uploader__result-message mpp-uploader__result-message--loading"
            : "mpp-uploader__result-message";
        element.textContent = message;
        this.projectResults.appendChild(element);
        this.projectResults.hidden = false;
        this.projectSearchInput.setAttribute("aria-expanded", "true");
    }

    private updateHighlightedProject(): void {
        const options = Array.from(this.projectResults.querySelectorAll<HTMLButtonElement>(".mpp-uploader__result"));
        options.forEach((option, index) => {
            const selected = index === this.highlightedProjectIndex;
            option.setAttribute("aria-selected", String(selected));
            if (selected) option.scrollIntoView({ block: "nearest" });
        });
    }

    private selectProject(project: ProjectOption): void {
        this.selectedProject = project;
        this.projectSearchInput.value = project.name;
        this.selectedProjectName.textContent = project.name;
        this.selectedProjectName.title = project.name;
        this.selectedProjectPanel.hidden = false;
        this.closeProjectResults();
        this.updateReadyState();
    }

    private closeProjectResults(): void {
        this.projectResults.hidden = true;
        this.projectSearchInput.setAttribute("aria-expanded", "false");
        this.highlightedProjectIndex = -1;
    }

    private readonly onSourceProjectSearchInput = (): void => {
        if (this.sourceSearchTimer !== null) window.clearTimeout(this.sourceSearchTimer);
        this.sourceSearchTimer = window.setTimeout(
            () => void this.loadSourceProjects(),
            PROJECT_SEARCH_DELAY_MS
        );
    };

    private readonly onSourceProjectSelectAllChange = (): void => {
        if (this.isBusy()) return;
        this.visibleSourceProjects.forEach((project) => {
            if (this.sourceProjectSelectAllCheckbox.checked) this.selectedSourceProjects.set(project.id, project);
            else this.selectedSourceProjects.delete(project.id);
        });
        this.renderSourceProjects();
        this.updateReadyState();
    };

    private readonly onDestinationEnvironmentChange = (): void => {
        const host = this.destinationEnvironmentSelect.value;
        this.selectedDestinationEnvironment =
            this.destinationEnvironments.find((environment) => environment.host === host) ?? null;
        this.updateReadyState();
    };

    /** Loads the active projects of the current environment, filtered by the search box. */
    private async loadSourceProjects(): Promise<void> {
        const sequence = ++this.sourceSearchSequence;
        this.isLoadingSourceProjects = true;
        this.showSourceProjectMessage("Loading projects…", "loading");
        this.applyDisabledState();
        try {
            const search = this.sourceProjectSearchInput.value.trim().replace(/'/g, "''");
            const filter = search
                ? `statecode eq 0 and contains(msdyn_subject,'${search}')`
                : "statecode eq 0";
            const response = await this.context.webAPI.retrieveMultipleRecords(
                "msdyn_project",
                "?$select=msdyn_projectid,msdyn_subject,msdyn_scheduledstart,msdyn_finish,_ownerid_value" +
                `&$filter=${filter}&$orderby=msdyn_subject asc`,
                SOURCE_PROJECT_PAGE_SIZE
            );
            if (sequence !== this.sourceSearchSequence) return;
            this.visibleSourceProjects = response.entities
                .map((entity): SourceProjectRow | null => {
                    const id = this.normalizeGuid(String(entity.msdyn_projectid ?? ""));
                    const name = String(entity.msdyn_subject ?? "").trim();
                    if (!id || !name) return null;
                    return {
                        id,
                        name,
                        startDate: this.toNullableString(entity.msdyn_scheduledstart),
                        finishDate: this.toNullableString(entity.msdyn_finish),
                        ownerName: this.toNullableString(
                            entity["_ownerid_value@OData.Community.Display.V1.FormattedValue"]
                        )
                    };
                })
                .filter((project): project is SourceProjectRow => project !== null);
            this.renderSourceProjects();
            const hasMore = Boolean(response.nextLink);
            this.showSourceProjectMessage(
                this.visibleSourceProjects.length === 0
                    ? "No active projects were found."
                    : hasMore
                        ? `Showing the first ${this.visibleSourceProjects.length} projects. Use the filter to narrow the list.`
                        : `${this.visibleSourceProjects.length} active ${this.visibleSourceProjects.length === 1 ? "project" : "projects"}.`,
                "info"
            );
        } catch (error: unknown) {
            if (sequence !== this.sourceSearchSequence) return;
            const detail = error instanceof Error && error.message ? ` ${error.message}` : "";
            this.showSourceProjectMessage(`Projects could not be retrieved.${detail}`, "error");
        } finally {
            if (sequence === this.sourceSearchSequence) this.isLoadingSourceProjects = false;
            this.applyDisabledState();
        }
    }

    private renderSourceProjects(): void {
        if (!this.sourceProjectGridBody) return;
        this.sourceProjectGridBody.replaceChildren();
        const locked = this.isDisabled || this.isBusy();

        this.visibleSourceProjects.forEach((project) => {
            const selected = this.selectedSourceProjects.has(project.id);
            const row = document.createElement("tr");
            row.className = "mpp-uploader__pwa-project-row";
            row.classList.toggle("mpp-uploader__pwa-project-row--selected", selected);

            const selectCell = document.createElement("td");
            const checkbox = document.createElement("input");
            checkbox.className = "mpp-uploader__checkbox mpp-uploader__source-row-checkbox";
            checkbox.type = "checkbox";
            checkbox.checked = selected;
            checkbox.disabled = locked;
            checkbox.setAttribute("aria-label", `Select ${project.name}`);
            checkbox.addEventListener("change", () => {
                if (checkbox.checked) this.selectedSourceProjects.set(project.id, project);
                else this.selectedSourceProjects.delete(project.id);
                row.classList.toggle("mpp-uploader__pwa-project-row--selected", checkbox.checked);
                this.updateSourceSelectionSummary();
                this.updateReadyState();
            });
            selectCell.appendChild(checkbox);

            const nameCell = document.createElement("td");
            nameCell.className = "mpp-uploader__pwa-project-name";
            nameCell.textContent = project.name;
            nameCell.title = project.name;

            row.append(
                selectCell,
                nameCell,
                this.createPwaTextCell(this.formatPwaDate(project.startDate)),
                this.createPwaTextCell(this.formatPwaDate(project.finishDate)),
                this.createPwaTextCell(project.ownerName ?? "—")
            );
            this.sourceProjectGridBody.appendChild(row);
        });
        this.updateSourceSelectionSummary();
    }

    private updateSourceSelectionSummary(): void {
        const selectedCount = this.selectedSourceProjects.size;
        const visibleSelected = this.visibleSourceProjects
            .filter((project) => this.selectedSourceProjects.has(project.id)).length;
        const visibleCount = this.visibleSourceProjects.length;
        this.sourceProjectSelectionCount.textContent = selectedCount === 0
            ? "No projects selected"
            : `${selectedCount} ${selectedCount === 1 ? "project" : "projects"} selected`;
        this.sourceProjectSelectAllCheckbox.checked = visibleCount > 0 && visibleSelected === visibleCount;
        this.sourceProjectSelectAllCheckbox.indeterminate = visibleSelected > 0 && visibleSelected < visibleCount;
        this.sourceProjectSelectAllCheckbox.disabled =
            this.isDisabled || this.isBusy() || visibleCount === 0;
    }

    private showSourceProjectMessage(
        message: string,
        type: "info" | "loading" | "success" | "error"
    ): void {
        this.sourceProjectGridMessage.className =
            `mpp-uploader__pwa-message mpp-uploader__pwa-message--${type}`;
        this.sourceProjectGridMessage.textContent = message;
    }

    /**
     * Fills the "Destination environment" dropdown. The list comes from the Azure Function
     * (GET environmentsFunctionUrl) or, when that is not configured, from the static JSON in
     * targetEnvironments. The environment this control runs in is never offered.
     */
    private async loadDestinationEnvironments(force = false): Promise<void> {
        if (this.isLoadingEnvironments || this.isBusy() || (this.environmentsLoaded && !force)) return;
        this.isLoadingEnvironments = true;
        this.renderDestinationEnvironments();
        this.setEnvironmentMessage("Loading environments…", false);
        this.applyDisabledState();

        const controller = new AbortController();
        this.environmentsController = controller;
        try {
            const endpoint = (this.context.parameters.environmentsFunctionUrl.raw ?? "").trim();
            let items: unknown[];
            if (endpoint) {
                if (!this.isValidHttpsUrl(endpoint)) {
                    throw new Error("Configure a valid HTTPS URL in the Environments Function URL property.");
                }
                const timeoutId = window.setTimeout(() => controller.abort(), ENVIRONMENTS_REQUEST_TIMEOUT_MS);
                try {
                    const response = await fetch(this.buildEnvironmentsUrl(endpoint, force), {
                        method: "GET",
                        headers: { "Accept": "application/json" },
                        signal: controller.signal
                    });
                    const body = this.parseResponseText(await response.text());
                    if (!response.ok) throw new Error(this.extractErrorMessage(body, response.status));
                    items = this.extractPwaArray(body, ["environments", "Environments", "value"]);
                } finally {
                    window.clearTimeout(timeoutId);
                }
            } else {
                items = this.parseStaticEnvironments();
            }

            const current = this.getHostEnvironment();
            const unique = new Map<string, EnvironmentTarget>();
            items.forEach((item) => {
                const environment = this.normalizeEnvironment(item);
                if (environment && environment.host !== current?.host) unique.set(environment.host, environment);
            });
            this.destinationEnvironments = Array.from(unique.values())
                .sort((left, right) => left.name.localeCompare(right.name));
            this.environmentsLoaded = true;
            this.isLoadingEnvironments = false;

            if (this.selectedDestinationEnvironment &&
                !unique.has(this.selectedDestinationEnvironment.host)) {
                this.selectedDestinationEnvironment = null;
            }
            this.renderDestinationEnvironments();
            this.setEnvironmentMessage(
                this.destinationEnvironments.length === 0
                    ? "No destination environments are available. Configure the Environments Function URL " +
                      "or the Target Environments property."
                    : `${this.destinationEnvironments.length} destination ` +
                      `${this.destinationEnvironments.length === 1 ? "environment" : "environments"} available.`,
                this.destinationEnvironments.length === 0
            );
        } catch (error: unknown) {
            const message = error instanceof DOMException && error.name === "AbortError"
                ? "The environments request timed out."
                : error instanceof Error && error.message
                    ? error.message
                    : "The destination environments could not be loaded.";
            this.isLoadingEnvironments = false;
            this.renderDestinationEnvironments();
            this.setEnvironmentMessage(message, true);
        } finally {
            this.environmentsController = null;
            this.isLoadingEnvironments = false;
            this.applyDisabledState();
            this.updateReadyState();
        }
    }

    /**
     * The request tells the Function which environment this control runs in. With automatic discovery
     * on, that is how it knows which credentials to read the tenant's environments with. Refresh also
     * asks it to skip its cache. The ?code= key that is already in the URL is kept.
     */
    private buildEnvironmentsUrl(endpoint: string, refresh: boolean): string {
        const current = this.getHostEnvironment();
        if (!current && !refresh) return endpoint;

        const url = new URL(endpoint);
        if (current) {
            url.searchParams.set("environmentUrl", current.environmentUrl);
            url.searchParams.set("environmentApiUrl", current.environmentApiUrl);
            url.searchParams.set("cloud", current.cloud);
        }
        if (refresh) url.searchParams.set("refresh", "1");
        return url.toString();
    }

    private parseStaticEnvironments(): unknown[] {
        const raw = (this.context.parameters.targetEnvironments.raw ?? "").trim();
        if (!raw) return [];
        try {
            return this.extractPwaArray(JSON.parse(raw) as unknown, ["environments", "value"]);
        } catch {
            throw new Error("The Target Environments property must be a JSON array, e.g. " +
                "[{\"name\":\"UAT\",\"url\":\"https://org.crm.dynamics.com\"}].");
        }
    }

    private normalizeEnvironment(item: unknown): EnvironmentTarget | null {
        const value = this.asObject(item);
        if (!value) return null;
        const url = this.getFirstPwaString(value, [
            "environmentUrl", "url", "instanceUrl", "instanceApiUrl", "apiUrl", "EnvironmentUrl", "Url"
        ]);
        if (!url) return null;
        const parsed = this.parseEnvironmentUrl(url);
        if (!parsed) return null;
        const name = this.getFirstPwaString(value, [
            "name", "displayName", "friendlyName", "Name", "DisplayName"
        ]) ?? parsed.host;
        return { ...parsed, name };
    }

    private renderDestinationEnvironments(): void {
        if (!this.destinationEnvironmentSelect) return;
        this.destinationEnvironmentSelect.replaceChildren();
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = this.isLoadingEnvironments
            ? "Loading environments…"
            : "Select a destination environment…";
        this.destinationEnvironmentSelect.appendChild(placeholder);
        this.destinationEnvironments.forEach((environment) => {
            const option = document.createElement("option");
            option.value = environment.host;
            option.textContent = `${environment.name} (${environment.host})`;
            this.destinationEnvironmentSelect.appendChild(option);
        });
        this.destinationEnvironmentSelect.value = this.selectedDestinationEnvironment?.host ?? "";
    }

    private setEnvironmentMessage(message: string, isError: boolean): void {
        this.destinationEnvironmentMessage.textContent = message;
        this.destinationEnvironmentMessage.classList.toggle("mpp-uploader__field-hint--error", isError);
    }

    private toNullableString(value: unknown): string | null {
        return typeof value === "string" && value.trim() ? value.trim() : null;
    }

    private readonly onSelectFileClick = (event: MouseEvent): void => {
        event.stopPropagation();
        this.openFilePicker();
    };
    private readonly onDropZoneClick = (): void => this.openFilePicker();
    private readonly onDropZoneKeyDown = (event: KeyboardEvent): void => {
        if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            this.openFilePicker();
        }
    };
    private readonly onFileInputChange = (): void => {
        const file = this.fileInput.files?.item(0) ?? null;
        if (file) this.selectFile(file);
    };
    private readonly onDragOver = (event: DragEvent): void => {
        event.preventDefault();
        if (!this.isDisabled && !this.isBusy()) {
            this.dropZone.classList.add("mpp-uploader__drop-zone--active");
            if (event.dataTransfer) event.dataTransfer.dropEffect = "copy";
        }
    };
    private readonly onDragLeave = (): void => this.dropZone.classList.remove("mpp-uploader__drop-zone--active");
    private readonly onDrop = (event: DragEvent): void => {
        event.preventDefault();
        this.dropZone.classList.remove("mpp-uploader__drop-zone--active");
        if (this.isDisabled || this.isBusy()) return;
        const file = event.dataTransfer?.files.item(0) ?? null;
        if (file) this.selectFile(file);
    };
    private readonly onClearFileClick = (): void => this.clearFileSelection();
    private readonly onUploadClick = (): void => void this.startAction();
    private readonly onTestConnectionClick = (): void => void this.testConnection();

    private openFilePicker(): void {
        if (!this.isDisabled && !this.isBusy()) this.fileInput.click();
    }

    private selectFile(file: File): void {
        const validationError = this.validateFile(file);
        if (validationError) {
            this.clearFileSelection(false);
            this.hideOperationDetails();
            this.setState("error", validationError);
            return;
        }
        this.selectedFile = file;
        this.fileNameElement.textContent = file.name;
        this.fileNameElement.title = file.name;
        this.fileSizeElement.textContent = this.formatFileSize(file.size);
        this.filePanel.hidden = false;
        this.dropZone.hidden = true;
        this.updateReadyState();
    }

    private validateFile(file: File): string | null {
        const expectedExtension = this.importMode === "excel" ? ".xlsx" : ".mpp";
        if (!file.name.toLowerCase().endsWith(expectedExtension)) {
            return `The selected file must have an ${expectedExtension} extension.`;
        }
        if (file.size === 0) return "The selected file is empty.";
        const maxFileSizeMb = this.getPositiveNumber(this.context.parameters.maxFileSizeMb.raw, DEFAULT_MAX_FILE_SIZE_MB);
        return file.size > maxFileSizeMb * BYTES_PER_MB
            ? `The file exceeds the configured maximum size of ${maxFileSizeMb} MB.`
            : null;
    }

    private clearFileSelection(resetStatus = true): void {
        if (this.isBusy()) return;
        this.selectedFile = null;
        this.fileInput.value = "";
        this.filePanel.hidden = true;
        this.dropZone.hidden = false;
        if (resetStatus) this.updateReadyState();
        else this.applyDisabledState();
    }

    private updateReadyState(): void {
        if (this.isBusy()) return;
        if (this.importMode === "projectOnline") {
            this.setState("idle", "");
        } else if (this.importMode === "project") {
            const count = this.selectedSourceProjects.size;
            const environment = this.selectedDestinationEnvironment;
            if (count > 0 && environment) {
                this.setState(
                    "ready",
                    `Ready to copy ${count} ${count === 1 ? "project" : "projects"} to “${environment.name}”.`
                );
            } else if (count === 0 && !environment) {
                this.setState("idle", "Select at least one source project and a destination environment.");
            } else if (count === 0) {
                this.setState("idle", "Now select at least one source project.");
            } else {
                this.setState("idle", "Now select the destination environment.");
            }
        } else if (this.selectedProject && this.selectedFile) {
            this.setState("ready", `Ready to import tasks into “${this.selectedProject.name}”.`);
        } else if (!this.selectedProject && !this.selectedFile) {
            this.setState(
                "idle",
                this.importMode === "excel"
                    ? "Select a destination project and an Excel file."
                    : "Select a destination project and a Microsoft Project file."
            );
        } else if (!this.selectedProject) {
            this.setState("idle", "Now select the destination project.");
        } else {
            this.setState(
                "idle",
                this.importMode === "excel"
                    ? "Now select the .xlsx file you want to import."
                    : "Now select the .mpp file you want to import."
            );
        }
        this.applyDisabledState();
    }

    private async startAction(): Promise<void> {
        if (this.importMode === "projectOnline") return;
        if (this.importMode === "project") {
            await this.startProjectCopy();
            return;
        }
        await this.startFileImport();
    }

    private async startFileImport(): Promise<void> {
        const project = this.selectedProject;
        const file = this.selectedFile;
        if (!project || !file || this.isBusy()) return;

        this.hideOperationDetails();
        const isExcelMode = this.importMode === "excel";
        const rawFunctionUrl = isExcelMode
            ? this.context.parameters.excelFunctionUrl.raw
            : this.context.parameters.functionUrl.raw;
        const functionUrl = (rawFunctionUrl ?? "").trim();
        if (!this.isValidHttpsUrl(functionUrl)) {
            this.setState(
                "error",
                isExcelMode
                    ? "Configure a valid HTTPS URL in the Excel Function URL property."
                    : "Configure a valid HTTPS URL for ImportMppToProject."
            );
            return;
        }
        const environment = this.getHostEnvironment();
        if (!environment) {
            this.setState("error", this.getHostEnvironmentError());
            return;
        }
        const functionKey = isExcelMode
            ? ""
            : this.decodeFunctionKey((this.context.parameters.functionKey.raw ?? "").trim());
        const timeoutSeconds = this.getPositiveNumber(
            this.context.parameters.requestTimeoutSeconds.raw,
            DEFAULT_TIMEOUT_SECONDS
        );
        const correlationId = this.createCorrelationId();

        this.setState("uploading", "Uploading the file to Azure…");
        this.showOperationDetails(correlationId, null);
        this.setProgress(0, `Preparing ${this.formatFileSize(file.size)}…`, false);
        this.applyDisabledState();

        try {
            const result = await this.uploadFile(
                functionUrl,
                functionKey,
                project.id,
                file,
                environment,
                correlationId,
                timeoutSeconds
            );
            if (result.status < 200 || result.status >= 300) {
                throw new Error(this.extractErrorMessage(result.body, result.status));
            }
            const accepted = this.asObject(result.body);
            this.showAcceptedDetails(environment, accepted, correlationId);
            const statusRequest = this.getStatusRequest(accepted, functionUrl, functionKey);
            if (statusRequest) {
                await this.monitorOperation(statusRequest, project.name, this.importMode);
            } else {
                this.setState("success", "The file was received and the import started successfully.");
                this.hideProgress();
                this.finishSuccessfulImport();
            }
        } catch (error: unknown) {
            this.hideProgress();
            this.setState("error", this.getErrorMessage(error, timeoutSeconds));
        } finally {
            this.activeRequest = null;
            this.pollingController = null;
            this.applyDisabledState();
        }
    }

    /**
     * "Copy project tasks": validates the selection, then walks the selected projects one at a
     * time. Each project gets its own POST to the Azure Function (sourceProjectId + target
     * environment) and is followed until the orchestration ends, so a failure in one project
     * is reported without cancelling the others.
     */
    private async startProjectCopy(): Promise<void> {
        if (this.isBusy()) return;

        const sourceProjects = Array.from(this.selectedSourceProjects.values());
        const targetEnvironment = this.selectedDestinationEnvironment;
        this.hideOperationDetails();
        this.clearCopyResults();

        if (sourceProjects.length === 0) {
            this.failValidation("Select at least one source project.");
            return;
        }
        if (!targetEnvironment) {
            this.failValidation("Select a destination environment.");
            return;
        }
        const functionUrl = (this.context.parameters.copyProjectFunctionUrl.raw ?? "").trim();
        if (!this.isValidHttpsUrl(functionUrl)) {
            this.failValidation("Configure a valid HTTPS URL in the Copy Project Function URL property.");
            return;
        }
        const sourceEnvironment = this.getHostEnvironment();
        if (!sourceEnvironment) {
            this.failValidation(this.getHostEnvironmentError());
            return;
        }
        if (sourceEnvironment.host === targetEnvironment.host) {
            this.failValidation("The destination environment must be different from the current one.");
            return;
        }

        const timeoutSeconds = this.getPositiveNumber(
            this.context.parameters.requestTimeoutSeconds.raw,
            DEFAULT_TIMEOUT_SECONDS
        );
        const total = sourceProjects.length;
        const outcomes: ProjectCopyOutcome[] = [];

        this.setState("uploading", `Starting the copy of ${total} ${total === 1 ? "project" : "projects"}…`);
        this.setProgress(0, `0 of ${total} completed`, false);
        this.applyDisabledState();

        for (let index = 0; index < total; index++) {
            if (this.isDisposed) return;
            const project = sourceProjects[index];
            this.setState("uploading", `Sending “${project.name}” (${index + 1} of ${total}) to Azure…`);
            this.setProgress(
                Math.round((index / total) * 100),
                `${index} of ${total} completed`,
                false
            );

            const outcome = await this.copySingleProject(
                project,
                sourceEnvironment,
                targetEnvironment,
                functionUrl,
                timeoutSeconds,
                `(${index + 1} of ${total})`
            );
            if (this.isDisposed) return;
            outcomes.push(outcome);
            this.appendCopyResult(outcome);
        }

        this.pollingController = null;
        this.hideProgress();
        this.finishProjectCopy(outcomes, targetEnvironment);
    }

    /** One project, one POST. Never throws: the outcome says whether it worked. */
    private async copySingleProject(
        project: SourceProjectRow,
        sourceEnvironment: HostEnvironment,
        targetEnvironment: EnvironmentTarget,
        functionUrl: string,
        timeoutSeconds: number,
        position: string
    ): Promise<ProjectCopyOutcome> {
        const controller = new AbortController();
        this.pollingController = controller;
        const correlationId = this.createCorrelationId();
        const payload: CopyProjectPayload = {
            sourceProjectId: project.id,
            targetEnvironment: {
                name: targetEnvironment.name,
                environmentUrl: targetEnvironment.environmentUrl,
                environmentApiUrl: targetEnvironment.environmentApiUrl,
                cloud: targetEnvironment.cloud
            },
            environmentUrl: sourceEnvironment.environmentUrl,
            environmentApiUrl: sourceEnvironment.environmentApiUrl,
            cloud: sourceEnvironment.cloud,
            correlationId
        };

        try {
            const timeoutId = window.setTimeout(() => controller.abort(), timeoutSeconds * 1000);
            let response: Response;
            try {
                response = await fetch(functionUrl, {
                    method: "POST",
                    headers: {
                        "Accept": "application/json",
                        "Content-Type": "application/json"
                    },
                    body: JSON.stringify(payload),
                    signal: controller.signal
                });
            } finally {
                window.clearTimeout(timeoutId);
            }

            const body = this.parseResponseText(await response.text());
            if (!response.ok) throw new Error(this.extractErrorMessage(body, response.status));

            const accepted = this.asObject(body);
            const confirmedCorrelationId = (accepted ? this.getString(accepted, "correlationId") : null) ??
                correlationId;
            const statusRequest = this.getStatusRequest(accepted, functionUrl, "");
            if (!statusRequest) {
                return this.createOutcome(
                    project, true, `Copy to “${targetEnvironment.name}” started.`, confirmedCorrelationId
                );
            }

            this.setState("processing", `Copying “${project.name}” ${position}…`);
            this.setProgress(100, `Copying “${project.name}” ${position}…`, true);
            const status = await this.pollOperation(
                statusRequest,
                controller.signal,
                (message) => this.setState("processing", `“${project.name}” ${position}: ${message}`)
            );
            const output = this.asObject(status.output);
            const message = (output ? this.getString(output, "message") : null) ??
                `Copied to “${targetEnvironment.name}”.`;
            return this.createOutcome(project, true, message, confirmedCorrelationId);
        } catch (error: unknown) {
            const message = error instanceof DOMException && error.name === "AbortError"
                ? `The request exceeded ${timeoutSeconds} seconds or was canceled.`
                : this.getErrorMessage(error, timeoutSeconds);
            return this.createOutcome(project, false, message, correlationId);
        }
    }

    private createOutcome(
        project: SourceProjectRow,
        succeeded: boolean,
        message: string,
        correlationId: string | null
    ): ProjectCopyOutcome {
        return { projectId: project.id, projectName: project.name, succeeded, message, correlationId };
    }

    private finishProjectCopy(outcomes: ProjectCopyOutcome[], targetEnvironment: EnvironmentTarget): void {
        const total = outcomes.length;
        const succeeded = outcomes.filter((outcome) => outcome.succeeded);
        const failed = total - succeeded.length;

        // Copied projects leave the selection so a retry cannot create them twice in the destination.
        succeeded.forEach((outcome) => this.selectedSourceProjects.delete(outcome.projectId));
        this.renderSourceProjects();

        if (failed === 0) {
            this.setState(
                "success",
                `${total} ${total === 1 ? "project was" : "projects were"} copied to “${targetEnvironment.name}”.`
            );
        } else if (succeeded.length === 0) {
            this.setState(
                "error",
                `None of the ${total} ${total === 1 ? "project" : "projects"} could be copied. See the details below.`
            );
        } else {
            this.setState(
                "error",
                `${succeeded.length} of ${total} projects were copied to “${targetEnvironment.name}”; ` +
                `${failed} failed and ${failed === 1 ? "stays" : "stay"} selected. See the details below.`
            );
        }
        this.applyDisabledState();
    }

    private failValidation(message: string): void {
        this.setState("error", message);
        this.applyDisabledState();
    }

    private appendCopyResult(outcome: ProjectCopyOutcome): void {
        const item = document.createElement("li");
        item.className = outcome.succeeded
            ? "mpp-uploader__copy-result mpp-uploader__copy-result--success"
            : "mpp-uploader__copy-result mpp-uploader__copy-result--error";
        const icon = document.createElement("span");
        icon.className = "mpp-uploader__copy-result-icon";
        icon.setAttribute("aria-hidden", "true");
        icon.textContent = outcome.succeeded ? "✓" : "✕";
        const text = document.createElement("span");
        text.className = "mpp-uploader__copy-result-text";
        const name = document.createElement("strong");
        name.textContent = outcome.projectName;
        text.append(name, ` — ${outcome.message}`);
        if (outcome.correlationId) {
            const id = document.createElement("code");
            id.className = "mpp-uploader__correlation-id";
            id.textContent = outcome.correlationId;
            text.append(" ", id);
        }
        item.append(icon, text);
        this.copyResults.appendChild(item);
        this.copyResults.hidden = false;
    }

    private clearCopyResults(): void {
        if (!this.copyResults) return;
        this.copyResults.replaceChildren();
        this.copyResults.hidden = true;
    }

    private async testConnection(): Promise<void> {
        if (this.isTestingConnection || this.isBusy() || this.isDisabled) return;

        const functionUrl = (this.context.parameters.testConnectionFunctionUrl.raw ?? "").trim();
        if (!this.isValidHttpsUrl(functionUrl)) {
            this.showConnectionResult(
                "error",
                "Configure a valid HTTPS URL in the Test Connection Function URL property."
            );
            return;
        }
        const environment = this.getHostEnvironment();
        if (!environment) {
            this.showConnectionResult("error", this.getHostEnvironmentError());
            return;
        }

        this.isTestingConnection = true;
        const controller = new AbortController();
        this.testConnectionController = controller;
        this.testConnectionButton.textContent = "Testing connection…";
        this.showConnectionResult("pending", "Connecting to the Azure Function…");
        this.applyDisabledState();

        const timeoutId = window.setTimeout(
            () => controller.abort(),
            TEST_CONNECTION_TIMEOUT_MS
        );

        try {
            const response = await fetch(this.appendEnvironmentQuery(functionUrl, environment), {
                method: "GET",
                headers: { "Accept": "application/json, text/plain;q=0.9, */*;q=0.8" },
                cache: "no-store",
                signal: controller.signal
            });
            const responseText = await response.text();
            const formattedResponse = this.formatConnectionResponse(responseText);
            const statusLine = `HTTP ${response.status} ${response.statusText || ""}`.trim();

            this.showConnectionResult(
                response.ok ? "success" : "error",
                `${statusLine}\n${formattedResponse}`
            );
        } catch (error: unknown) {
            const message = error instanceof DOMException && error.name === "AbortError"
                ? "The connection test timed out after 30 seconds."
                : error instanceof Error && error.message
                    ? error.message
                    : "The Azure Function could not be reached.";
            this.showConnectionResult(
                "error",
                `Connection failed\n${message}\nCheck the endpoint URL, Function Key, CORS, and network access.`
            );
        } finally {
            window.clearTimeout(timeoutId);
            this.testConnectionController = null;
            this.isTestingConnection = false;
            this.testConnectionButton.textContent = "Test connection";
            this.applyDisabledState();
        }
    }

    private showConnectionResult(
        state: "pending" | "success" | "error",
        message: string
    ): void {
        if (this.testConnectionResultTimer !== null) {
            window.clearTimeout(this.testConnectionResultTimer);
            this.testConnectionResultTimer = null;
        }
        this.testConnectionResult.hidden = false;
        this.testConnectionResult.className =
            `mpp-uploader__connection-result mpp-uploader__connection-result--${state}`;
        this.testConnectionResult.textContent = message;

        if (state !== "pending") {
            this.testConnectionResultTimer = window.setTimeout(() => {
                this.testConnectionResult.hidden = true;
                this.testConnectionResult.textContent = "";
                this.testConnectionResultTimer = null;
            }, TEST_CONNECTION_RESULT_VISIBILITY_MS);
        }
    }

    private formatConnectionResponse(responseText: string): string {
        if (!responseText.trim()) return "The endpoint returned an empty response.";

        const parsed = this.parseResponseText(responseText);
        if (typeof parsed === "string") return parsed;

        try {
            return JSON.stringify(parsed, null, 2);
        } catch {
            return responseText;
        }
    }

    private uploadFile(
        functionUrl: string,
        functionKey: string,
        targetProjectId: string,
        file: File,
        environment: HostEnvironment,
        correlationId: string,
        timeoutSeconds: number
    ): Promise<HttpResult> {
        return new Promise<HttpResult>((resolve, reject) => {
            const request = new XMLHttpRequest();
            this.activeRequest = request;
            request.open("POST", functionUrl, true);
            request.timeout = timeoutSeconds * 1000;
            request.setRequestHeader("Accept", "application/json");
            if (functionKey) request.setRequestHeader("x-functions-key", functionKey);

            request.upload.onprogress = (event: ProgressEvent<EventTarget>): void => {
                if (!event.lengthComputable) {
                    this.setProgress(0, "Uploading the file…", true);
                    return;
                }
                const percentage = Math.min(100, Math.round((event.loaded / event.total) * 100));
                this.setProgress(percentage, `Uploading file… ${percentage}%`, false);
            };
            request.onload = (): void => resolve({
                status: request.status,
                body: this.parseResponseText(request.responseText)
            });
            request.onerror = (): void => reject(
                new Error("Azure could not be reached. Check the URL, CORS settings, and network connection.")
            );
            request.ontimeout = (): void => reject(
                new Error(`The upload exceeded the ${timeoutSeconds}-second timeout.`)
            );
            request.onabort = (): void => reject(new Error("The file upload was canceled."));

            const formData = new FormData();
            formData.append("targetProjectId", targetProjectId);
            formData.append("environmentUrl", environment.environmentUrl);
            formData.append("environmentApiUrl", environment.environmentApiUrl);
            formData.append("cloud", environment.cloud);
            formData.append("correlationId", correlationId);
            formData.append("file", file, file.name);
            request.send(formData);
        });
    }

    // The Function answers 202 with statusUri, its own status endpoint, which takes the same
    // function key as the request: the header key, or the ?code= key of the configured URL.
    // An older Function sent the Durable statusQueryGetUri instead, which carries its own key.
    private getStatusRequest(
        accepted: Record<string, unknown> | null,
        requestUrl: string,
        functionKey: string
    ): StatusRequest | null {
        const statusUri = accepted ? this.getString(accepted, "statusUri") : null;
        if (statusUri && this.isValidHttpsUrl(statusUri)) {
            const url = new URL(statusUri);
            const code = new URL(requestUrl).searchParams.get("code");
            if (code && !url.searchParams.has("code")) url.searchParams.set("code", code);
            return { url: url.toString(), functionKey: url.searchParams.has("code") ? "" : functionKey };
        }

        const legacyUri = accepted ? this.getString(accepted, "statusQueryGetUri") : null;
        return legacyUri && this.isValidHttpsUrl(legacyUri) ? { url: legacyUri, functionKey: "" } : null;
    }

    private async monitorOperation(
        statusRequest: StatusRequest,
        projectName: string,
        mode: ImportMode
    ): Promise<void> {
        this.pollingController = new AbortController();
        const isProjectMode = mode === "project";
        this.setState(
            "processing",
            isProjectMode
                ? "Azure received the request and is copying the project tasks…"
                : "Azure received the file and is creating the tasks…"
        );
        this.setProgress(
            100,
            isProjectMode ? "Copying tasks in PSA…" : "Processing the plan in PSA…",
            true
        );
        const status = await this.pollOperation(
            statusRequest,
            this.pollingController.signal,
            (message) => this.setState("processing", message)
        );
        this.setState("success", this.buildCompletionMessage(status.output, projectName, mode));
        this.hideProgress();
        if (mode !== "project") this.finishSuccessfulImport();
    }

    /**
     * Polls the Durable Functions status endpoint until the orchestration ends. Returns the final
     * status when it completed and throws when it failed, was terminated or canceled.
     */
    private async pollOperation(
        statusRequest: StatusRequest,
        signal: AbortSignal,
        onStatusMessage?: (message: string) => void
    ): Promise<DurableStatusResponse> {
        while (true) {
            await this.delay(STATUS_POLL_INTERVAL_MS, signal);
            const headers: Record<string, string> = { "Accept": "application/json" };
            if (statusRequest.functionKey) headers["x-functions-key"] = statusRequest.functionKey;
            const response = await fetch(statusRequest.url, { method: "GET", headers, signal });
            const body = this.parseResponseText(await response.text());
            if (!response.ok) throw new Error(this.extractErrorMessage(body, response.status));
            const status = (this.asObject(body) ?? {}) as DurableStatusResponse;
            const runtimeStatus = status.runtimeStatus?.toLowerCase() ?? "";
            if (runtimeStatus === "completed") return status;
            if (["failed", "terminated", "canceled"].includes(runtimeStatus)) {
                const error = typeof status.error === "string" ? status.error.trim() : "";
                throw new Error(error || this.extractDurableFailure(status.output, runtimeStatus));
            }
            const customStatus = this.extractCustomStatus(status.customStatus);
            if (customStatus) onStatusMessage?.(customStatus);
        }
    }

    private finishSuccessfulImport(): void {
        this.selectedFile = null;
        this.fileInput.value = "";
        this.filePanel.hidden = true;
        this.dropZone.hidden = false;
    }

    private buildCompletionMessage(
        output: unknown,
        projectName: string,
        mode: ImportMode
    ): string {
        const value = this.asObject(output);
        if (value) {
            const message = this.getString(value, "message");
            const skipped = Array.isArray(value.skippedResources) ? value.skippedResources.length : 0;
            if (message) {
                return skipped > 0
                    ? `${message} ${skipped} ${skipped === 1 ? "resource was" : "resources were"} left out.`
                    : message;
            }
            const tasks = this.getNumber(value, "tasksCreated");
            const dependencies = this.getNumber(value, "dependenciesCreated");
            if (tasks !== null || dependencies !== null) {
                return `${mode === "project" ? "Copy" : "Import"} completed in “${projectName}”: ` +
                    `${tasks ?? 0} tasks and ` +
                    `${dependencies ?? 0} dependencies created.`;
            }
        }
        return mode === "project"
            ? `The project tasks were copied into “${projectName}” successfully.`
            : `The import into “${projectName}” completed successfully.`;
    }

    private extractDurableFailure(output: unknown, runtimeStatus: string): string {
        if (typeof output === "string" && output.trim()) return output.trim();
        const value = this.asObject(output);
        if (value) {
            const message = this.getString(value, "message") ?? this.getString(value, "error") ??
                this.getString(value, "details");
            if (message) return message;
        }
        return `The import ended with status ${runtimeStatus}. Check the Function App logs.`;
    }

    private extractCustomStatus(customStatus: unknown): string | null {
        if (typeof customStatus === "string" && customStatus.trim()) return customStatus.trim();
        const value = this.asObject(customStatus);
        return value ? this.getString(value, "message") : null;
    }

    private extractErrorMessage(body: unknown, statusCode: number): string {
        if (typeof body === "string" && body.trim()) return body.trim();
        const value = this.asObject(body);
        if (value) {
            const message = this.getString(value, "message") ?? this.getString(value, "error") ??
                this.getString(value, "detail") ?? this.getString(value, "title");
            if (message) return message;
        }
        if (statusCode === 400) return "The request is invalid. Check the selected project and file.";
        if (statusCode === 401 || statusCode === 403) {
            return "Azure rejected the authentication request. Check the configured Function Key.";
        }
        if (statusCode === 413) return "The file exceeds the maximum size allowed by the Function App.";
        return `The Azure Function returned HTTP status ${statusCode}.`;
    }

    private getErrorMessage(error: unknown, timeoutSeconds: number): string {
        if (error instanceof DOMException && error.name === "AbortError") {
            return "Import status monitoring was canceled.";
        }
        if (error instanceof Error && error.message.trim()) return error.message;
        return `An unexpected error occurred. Configured upload timeout: ${timeoutSeconds} seconds.`;
    }

    private setState(state: ControlState, message: string): void {
        this.state = state;
        if (state === "idle" || state === "ready") {
            this.hideOperationDetails();
            this.clearCopyResults();
        }
        this.statusElement.className = `mpp-uploader__status mpp-uploader__status--${state}`;
        this.statusElement.textContent = message;
        const isProjectMode = this.importMode === "project";
        this.uploadButton.textContent = state === "uploading"
            ? isProjectMode ? "Starting copy…" : "Uploading file…"
            : state === "processing"
                ? isProjectMode ? "Copying tasks…" : "Creating tasks…"
                : isProjectMode ? "Copy project tasks" : "Import tasks";
        this.container.setAttribute("aria-busy", String(this.isBusy()));
    }

    private setProgress(percentage: number, label: string, indeterminate: boolean): void {
        this.progressContainer.hidden = false;
        this.progressContainer.classList.toggle("mpp-uploader__progress--indeterminate", indeterminate);
        this.progressBar.style.width = indeterminate ? "34%" : `${percentage}%`;
        this.progressLabel.textContent = label;
        const track = this.progressBar.parentElement;
        if (track) {
            track.setAttribute("aria-valuenow", indeterminate ? "0" : String(percentage));
            track.setAttribute("aria-valuetext", label);
        }
    }

    private hideProgress(): void {
        this.progressContainer.hidden = true;
        this.progressContainer.classList.remove("mpp-uploader__progress--indeterminate");
        this.progressBar.style.width = "0";
    }

    private applyDisabledState(): void {
        const disabled = this.isDisabled || this.isBusy() || this.isTestingConnection;
        this.mppModeRadio.disabled = disabled;
        this.excelModeRadio.disabled = disabled;
        this.projectModeRadio.disabled = disabled;
        this.projectOnlineModeRadio.disabled = disabled;
        this.projectSearchInput.disabled = disabled;
        this.clearProjectButton.disabled = disabled;
        this.sourceProjectSearchInput.disabled = disabled;
        this.sourceProjectSelectAllCheckbox.disabled =
            disabled || this.isLoadingSourceProjects || this.visibleSourceProjects.length === 0;
        this.sourceProjectSection
            .querySelectorAll<HTMLInputElement>(".mpp-uploader__source-row-checkbox")
            .forEach((checkbox) => { checkbox.disabled = disabled; });
        this.destinationEnvironmentSelect.disabled = disabled || this.isLoadingEnvironments;
        this.refreshEnvironmentsButton.disabled = disabled || this.isLoadingEnvironments;
        this.fileInput.disabled = disabled;
        this.selectFileButton.disabled = disabled;
        this.clearFileButton.disabled = disabled;
        const missingRequiredInput = this.importMode === "projectOnline"
            ? true
            : this.importMode === "project"
                ? this.selectedSourceProjects.size === 0 || !this.selectedDestinationEnvironment
                : !this.selectedProject || !this.selectedFile;
        this.uploadButton.disabled = disabled || missingRequiredInput;
        this.testConnectionButton.disabled = disabled;
        this.dropZone.setAttribute("aria-disabled", String(disabled));
        this.dropZone.tabIndex = disabled ? -1 : 0;
        this.applyPwaDisabledState();
    }

    private isBusy(): boolean {
        return this.state === "uploading" || this.state === "processing";
    }

    private parseResponseText(text: string): unknown {
        if (!text.trim()) return null;
        try {
            return JSON.parse(text) as unknown;
        } catch {
            return text;
        }
    }

    private asObject(value: unknown): Record<string, unknown> | null {
        return typeof value === "object" && value !== null && !Array.isArray(value)
            ? value as Record<string, unknown>
            : null;
    }

    private getString(value: Record<string, unknown>, key: string): string | null {
        const property = value[key];
        return typeof property === "string" && property.trim() ? property.trim() : null;
    }

    private getNumber(value: Record<string, unknown>, key: string): number | null {
        const property = value[key];
        return typeof property === "number" && Number.isFinite(property) ? property : null;
    }

    private getHostEnvironment(): HostEnvironment | null {
        const clientUrl = this.getClientUrl();
        return clientUrl ? this.parseEnvironmentUrl(clientUrl) : null;
    }

    /** Derives the environment, API host and cloud from any Dataverse URL (null when it is not one). */
    private parseEnvironmentUrl(value: string): HostEnvironment | null {
        let url: URL;
        try {
            url = new URL(value);
        } catch {
            return null;
        }
        const host = url.hostname.toLowerCase();
        const match = DATAVERSE_CLOUDS.find((entry) => host.endsWith(entry.suffix));
        if (url.protocol !== "https:" || !match) return null;

        const labels = host.split(".");
        const isApiHost = labels[1] === "api";
        const environmentHost = isApiHost ? [labels[0], ...labels.slice(2)].join(".") : host;
        const apiHost = isApiHost ? host : [labels[0], "api", ...labels.slice(1)].join(".");
        return {
            environmentUrl: `https://${environmentHost}`,
            environmentApiUrl: `https://${apiHost}`,
            cloud: match.cloud,
            host: environmentHost
        };
    }

    private getClientUrl(): string | null {
        try {
            const page = (this.context as unknown as { page?: { getClientUrl?: () => string } }).page;
            const pageUrl = page?.getClientUrl?.();
            if (pageUrl) return pageUrl;
        } catch {
            // Fall back to the Client API of the hosting page.
        }

        const hosts: Window[] = [window];
        try {
            if (window.parent !== window) hosts.push(window.parent);
        } catch {
            // A parent page from another origin cannot be inspected.
        }
        for (const host of hosts) {
            try {
                const globalContext = (host as unknown as XrmGlobalContextHost).Xrm?.Utility?.getGlobalContext?.();
                const clientUrl = globalContext?.getClientUrl?.();
                if (clientUrl) return clientUrl;
            } catch {
                continue;
            }
        }
        return window.location.origin || null;
    }

    private getHostEnvironmentError(): string {
        return `The Dataverse environment of this page could not be determined (${window.location.host || "unknown host"}). ` +
            "Open the control in a model-driven app so the request goes to that environment.";
    }

    private renderTargetEnvironment(): void {
        const environment = this.getHostEnvironment();
        this.targetEnvironmentElement.className = environment
            ? "mpp-uploader__target-environment"
            : "mpp-uploader__target-environment mpp-uploader__target-environment--error";
        this.targetEnvironmentElement.textContent = environment
            ? `Environment: ${environment.host} (${environment.cloud})`
            : this.getHostEnvironmentError();
    }

    private createCorrelationId(): string {
        if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
        const bytes = crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        const hex = Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
        return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    }

    private appendEnvironmentQuery(functionUrl: string, environment: HostEnvironment): string {
        const url = new URL(functionUrl);
        const routing = new URLSearchParams({
            environmentUrl: environment.environmentUrl,
            environmentApiUrl: environment.environmentApiUrl,
            cloud: environment.cloud
        }).toString();
        url.search = url.search ? `${url.search}&${routing}` : `?${routing}`;
        return url.toString();
    }

    private showAcceptedDetails(
        environment: HostEnvironment,
        accepted: Record<string, unknown> | null,
        correlationId: string | null
    ): void {
        const confirmedUrl = accepted ? this.getString(accepted, "environment") : null;
        let confirmedHost: string | null = null;
        if (confirmedUrl) {
            try {
                confirmedHost = new URL(confirmedUrl).hostname.toLowerCase();
            } catch {
                confirmedHost = confirmedUrl.toLowerCase();
            }
        }
        const warning = !confirmedHost
            ? "The Azure Function did not confirm the environment. An older Function version imports into its own fixed environment."
            : confirmedHost !== environment.host
                ? `The Azure Function accepted the request for another environment: ${confirmedHost}.`
                : null;
        this.showOperationDetails(
            (accepted ? this.getString(accepted, "correlationId") : null) ?? correlationId,
            warning
        );
    }

    private showOperationDetails(correlationId: string | null, warning: string | null): void {
        if (!this.operationDetails) return;
        this.operationDetails.replaceChildren();
        if (correlationId) {
            const id = document.createElement("code");
            id.className = "mpp-uploader__correlation-id";
            id.title = "Quote this id when reporting a problem with this request.";
            id.textContent = correlationId;
            this.operationDetails.append("Correlation id: ", id);
        }
        if (warning) {
            const warningElement = document.createElement("span");
            warningElement.className = "mpp-uploader__operation-warning";
            warningElement.textContent = warning;
            this.operationDetails.append(warningElement);
        }
        this.operationDetails.hidden = !correlationId && !warning;
    }

    private hideOperationDetails(): void {
        if (!this.operationDetails) return;
        this.operationDetails.hidden = true;
        this.operationDetails.replaceChildren();
    }

    private isValidHttpsUrl(value: string): boolean {
        try {
            const url = new URL(value);
            return url.protocol === "https:" || url.hostname === "localhost";
        } catch {
            return false;
        }
    }

    private decodeFunctionKey(value: string): string {
        if (!value) return "";
        try {
            return decodeURIComponent(value);
        } catch {
            return value;
        }
    }

    private normalizeGuid(value: string): string | null {
        const normalized = value.trim().replace(/[{}]/g, "");
        const pattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
        return pattern.test(normalized) ? normalized : null;
    }

    private getPositiveNumber(value: number | null, fallback: number): number {
        return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : fallback;
    }

    private formatFileSize(bytes: number): string {
        if (bytes < 1024) return `${bytes} B`;
        if (bytes < BYTES_PER_MB) return `${(bytes / 1024).toFixed(1)} KB`;
        return `${(bytes / BYTES_PER_MB).toFixed(2)} MB`;
    }

    private delay(milliseconds: number, signal: AbortSignal): Promise<void> {
        return new Promise<void>((resolve, reject) => {
            const timeoutId = window.setTimeout(resolve, milliseconds);
            signal.addEventListener("abort", () => {
                window.clearTimeout(timeoutId);
                reject(new DOMException("Aborted", "AbortError"));
            }, { once: true });
        });
    }
}
