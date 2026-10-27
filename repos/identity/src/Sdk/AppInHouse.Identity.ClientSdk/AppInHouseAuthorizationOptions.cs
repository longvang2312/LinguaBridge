namespace AppInHouse.Identity.ClientSdk;

public class AppInHouseAuthorizationOptions
{
    /// <summary>Request header carrying the project code.</summary>
    public string ProjectCodeHeaderName { get; set; } = "X-Project-Code";

    /// <summary>Route value carrying the project code (e.g. <c>/api/{projectCode}/orders</c>).</summary>
    public string ProjectCodeRouteKey { get; set; } = "projectCode";
}
