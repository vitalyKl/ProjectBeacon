export type ActorType = "user" | "agent" | "system";

export type ActorRef = {
  type: ActorType;
  id: string;
  display: string;
};
