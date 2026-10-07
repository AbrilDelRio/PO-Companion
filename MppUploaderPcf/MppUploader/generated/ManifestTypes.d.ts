/*
*This is auto generated from the ControlManifest.Input.xml file
*/

// Define IInputs and IOutputs Type. They should match with ControlManifest.
export interface IInputs {
    functionUrl: ComponentFramework.PropertyTypes.StringProperty;
    functionKey: ComponentFramework.PropertyTypes.StringProperty;
    copyProjectFunctionUrl: ComponentFramework.PropertyTypes.StringProperty;
    environmentsFunctionUrl: ComponentFramework.PropertyTypes.StringProperty;
    targetEnvironments: ComponentFramework.PropertyTypes.StringProperty;
    excelFunctionUrl: ComponentFramework.PropertyTypes.StringProperty;
    testConnectionFunctionUrl: ComponentFramework.PropertyTypes.StringProperty;
    pwaProjectsFunctionUrl: ComponentFramework.PropertyTypes.StringProperty;
    pwaProjectTasksFunctionUrl: ComponentFramework.PropertyTypes.StringProperty;
    pwaConnectionPageName: ComponentFramework.PropertyTypes.StringProperty;
    maxFileSizeMb: ComponentFramework.PropertyTypes.WholeNumberProperty;
    requestTimeoutSeconds: ComponentFramework.PropertyTypes.WholeNumberProperty;
}
export interface IOutputs {
}
