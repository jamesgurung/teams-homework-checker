# Teams Homework Checker

This Azure Functions app checks which classes have homework set on Microsoft Teams and sends weekly summary emails.

## App Settings

1. `AzureWebJobsStorage` - set to the connection string on the storage account connected to the Azure Functions app
1. `FUNCTIONS_EXTENSION_VERSION` - set to `~4`
1. `FUNCTIONS_WORKER_RUNTIME` - set to `dotnet-isolated`
1. `WEBSITE_TIME_ZONE` - set your timezone, e.g. `GMT Standard Time`
1. `TenantId` - the Tenant ID of your Azure App Registration
1. `ClientId` - the Client ID of your Azure App Registration
1. `ClientSecret` - the Client Secret of your Azure App Registration
1. `PostmarkServerToken` - the Server Token of your Postmark account

## School Configuration

Create a `config` blob container. For each school to be configured, add the following files (note the naming convention of each file begins with a school code, which in this example is `xyz`):

### xyz-settings.json

```json
{
  "name": "Your School Name",
  "fromEmail": "Address from which to send summary emails",
  "seniorTeam": ["Array", "of", "teacher", "initials", "in", "SLT"],
  "replyTo": "Initials of the teacher to receive email replies",
  "classFilter": "Filter for listing educationClass resources, e.g.: startswith(externalId,'23_24_')",
  "defaultWeeks": 1,
  "customWeeks": [
    { "year": 7, "subject": "Cp", "weeks": 2 }
  ],
  "excludes": [
    { "year": 11, "subject": "Ma", "content": "Revise" }
  ]
}
```

### xyz-classes.csv

List each lesson of each class. In this example, the class `7a/Ar1` has two lessons with teacher `ABC` and one lesson with teacher `DEF`. Keep the duplicate rows as these are used to work out each class's main teacher.

```csv
Class,Staff Code
7a/Ar1,ABC
7a/Ar1,ABC
7a/Ar1,DEF
7a/Ar2,GHI
...
```

### xyz-days.csv

List all the working days of the academic year, in `yyyy-MM-dd` format.

```csv
Date
2023-09-06
2023-09-07
...
```

### xyz-departments.csv

List each department and the initials of its Curriculum Leader. Include the department's subjects as a semicolon-delimited list.

```csv
Department,CurriculumLeader,Subjects
Art,ABC,Ar;Pt;Tx
Business,GHI,Bu
...
```

### xyz-teachers.csv

List each teacher, their first name, and their email address.

```csv
Code,First,Email
ABC,Alison,abc@example.com
DEF,David,def@example.com
```